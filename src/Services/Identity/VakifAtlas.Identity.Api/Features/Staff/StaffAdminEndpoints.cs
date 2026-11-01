using Microsoft.EntityFrameworkCore;
using VakifAtlas.Identity.Api.Features.Auth;
using VakifAtlas.Identity.Api.Infrastructure;
using VakifAtlas.Identity.Domain;
using VakifAtlas.ServiceDefaults.Admin;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Identity.Api.Features.Staff;

/// <summary>
/// Personel yönetimi (<c>auth/admin/staff</c>, yalnız <c>vgm_admin</c>) ve merkezi denetim kaydı
/// (<c>auth/admin/audit</c>). Değiştiren istekler <c>If-Match</c> ister; yönetici kendi yönetici
/// rolünü kaldıramaz, kendini devre dışı bırakamaz ve sistemde etkin en az bir yönetici kalır.
/// </summary>
public static class StaffAdminEndpoints
{
    private const string AdminSetLock = "SELECT pg_advisory_xact_lock(hashtext('identity:staff-admins'))";

    public static RouteGroupBuilder MapStaffAdminEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("staff", List);
        group.MapGet("staff/{id}", Get);
        group.MapPost("staff", Invite).WithIdempotency<InviteStaffRequest>();
        group.MapPut("staff/{id}/roles", UpdateRoles);
        group.MapPost("staff/{id}/disable", (string id, HttpContext http, StaffAdminService service, CancellationToken ct) =>
            service.SetDisabledAsync(id, disabled: true, http, ct));
        group.MapPost("staff/{id}/enable", (string id, HttpContext http, StaffAdminService service, CancellationToken ct) =>
            service.SetDisabledAsync(id, disabled: false, http, ct));
        group.MapPost("staff/{id}/mfa-reset", ResetMfa);
        group.MapPost("staff/{id}/sessions/revoke", RevokeSessions);
        group.MapGet("audit", Audit).RequireAuthorization(StaffPolicies.Audit);
        return group;
    }

    private static async Task<IResult> List(HttpContext context, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        var limit = QueryParsing.Limit(query["limit"], 50, 100);
        var role = query["role"].ToString();
        var status = query["status"].ToString();
        var search = query["q"].ToString().Trim();
        if (limit is null || (role.Length > 0 && !StaffRoles.IsStaffRole(role)) || status is not ("" or "active" or "disabled") ||
            search.Length > 100)
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var staffRoles = StaffRoles.All.ToArray();
        var users = db.Users.AsNoTracking().Where(u => u.Roles.Any(r => staffRoles.Contains(r)));
        if (role.Length > 0)
        {
            users = users.Where(u => u.Roles.Contains(role));
        }

        users = status switch
        {
            "active" => users.Where(u => u.StaffDisabledAt == null),
            "disabled" => users.Where(u => u.StaffDisabledAt != null),
            _ => users,
        };
        if (search.Length > 0)
        {
            var pattern = $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            users = users.Where(u => EF.Functions.ILike(u.DisplayName, pattern) || EF.Functions.ILike(u.Email, pattern));
        }

        var total = await users.CountAsync(cancellationToken);
        if (query["cursor"].ToString() is { Length: > 0 } cursor)
        {
            if (CursorCodec.DecodeInstantId(cursor) is not var (createdAt, id))
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor).ToHttpResult();
            }

            users = users.Where(u => u.CreatedAt < createdAt || (u.CreatedAt == createdAt && string.Compare(u.Id, id) < 0));
        }

        var page = await users.OrderByDescending(u => u.CreatedAt).ThenByDescending(u => u.Id).Take(limit.Value + 1)
            .ToListAsync(cancellationToken);
        var next = page.Count > limit ? CursorCodec.EncodeInstantId(page[limit.Value - 1].CreatedAt, page[limit.Value - 1].Id) : null;
        return TypedResults.Ok(new Page<StaffMemberResponse>(page.Take(limit.Value).Select(ToMember).ToArray(), total, next));
    }

    private static async Task<IResult> Get(string id, HttpContext context, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null || !user.HasAnyRole(StaffRoles.All))
        {
            return ApiError.NotFound().ToHttpResult();
        }

        context.Response.SetETag(ETagOf(user));
        return TypedResults.Ok(ToMember(user));
    }

    private static async Task<IResult> Invite(InviteStaffRequest request, HttpContext context, IdentityDbContext db,
        AuthWorkflow workflow, PasswordHasher hasher, StaffAuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (request.Email is null || request.DisplayName is null || !Credentials.IsValidEmail(request.Email) ||
            !Credentials.IsValidDisplayName(request.DisplayName) || !ValidRoles(request.Roles, allowEmpty: false))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidStaff).ToHttpResult();
        }

        var email = Credentials.NormalizeEmail(request.Email);
        var roles = Normalize(request.Roles!);
        var now = clock.GetUtcNow();
        var user = await workflow.FindByEmailAsync(email, cancellationToken);
        if (user is null)
        {
            // Şifre bilinmeyen rastgele değerdir; personel "Şifremi unuttum" koduyla kendi şifresini belirler
            // (kod e-posta sahibine gittiğinden yönetici başkasının şifresini bilmez).
            user = new User
            {
                Id = Ids.New("usr", 9),
                Email = email,
                DisplayName = Credentials.NormalizeDisplayName(request.DisplayName),
                PasswordHash = await hasher.HashAsync(SecretTokens.NewRefreshToken(), cancellationToken),
                EmailVerified = true,
                CreatedAt = now,
                TermsVersion = "staff-invitation",
                PrivacyNoticeVersion = "staff-invitation",
                ConsentedAt = now,
                Roles = roles,
            };
            db.Users.Add(user);
        }
        else if (user.HasAnyRole(StaffRoles.All))
        {
            return ApiError.Conflict(ErrorCodes.InvalidStaff).ToHttpResult();
        }
        else
        {
            // Mevcut (vatandaş) hesaba personel rolü verilir; personel oturumu ayrıca MFA ister.
            user.EmailVerified = true;
            user.Roles = [.. user.Roles, .. roles];
            user.StaffDisabledAt = null;
            user.ResetMfa();
        }

        workflow.QueueEmail(email, EmailKind.StaffInvitation, RequestLanguage.Resolve(context.Request));
        audit.Record(context.User.RequiredUserId(), "staff.invited", "staff", user.Id,
            new Dictionary<string, string?> { ["roles"] = string.Join(',', roles) });
        await db.SaveChangesAsync(cancellationToken);
        context.Response.SetETag(ETagOf(user));
        return TypedResults.Created($"staff/{user.Id}", ToMember(user));
    }

    private static async Task<IResult> UpdateRoles(string id, StaffRolesRequest request, HttpContext context,
        StaffAdminService service, CancellationToken cancellationToken)
    {
        if (!ValidRoles(request.Roles, allowEmpty: true))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidStaff).ToHttpResult();
        }

        return await service.UpdateRolesAsync(id, Normalize(request.Roles!), context, cancellationToken);
    }

    private static Task<IResult> ResetMfa(string id, HttpContext context, StaffAdminService service,
        CancellationToken cancellationToken) =>
        service.MutateAsync(id, context, "staff.mfa-reset", forbidSelf: true, async (user, db) =>
        {
            user.ResetMfa();
            await db.RecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
            return null;
        }, revokeSessions: true, cancellationToken);

    private static Task<IResult> RevokeSessions(string id, HttpContext context, StaffAdminService service,
        CancellationToken cancellationToken) =>
        service.MutateAsync(id, context, "staff.sessions-revoked", forbidSelf: false, (_, _) => Task.FromResult<ApiError?>(null),
            revokeSessions: true, cancellationToken, requireIfMatch: false);

    private static async Task<IResult> Audit(HttpContext context, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        var limit = QueryParsing.Limit(query["limit"], 50, 200);
        var actor = query["actorId"].ToString();
        var service = query["service"].ToString();
        var targetType = query["targetType"].ToString();
        var targetId = query["targetId"].ToString();
        if (limit is null || actor.Length > 40 || service.Length > 40 || targetType.Length > 40 || targetId.Length > 80 ||
            !TryInstant(query["from"], out var from) || !TryInstant(query["to"], out var to))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var entries = db.AuditEntries.AsNoTracking();
        if (actor.Length > 0)
        {
            entries = entries.Where(e => e.ActorId == actor);
        }

        if (service.Length > 0)
        {
            entries = entries.Where(e => e.Service == service);
        }

        if (targetType.Length > 0)
        {
            entries = entries.Where(e => e.TargetType == targetType);
        }

        if (targetId.Length > 0)
        {
            entries = entries.Where(e => e.TargetId == targetId);
        }

        if (from is { } fromValue)
        {
            entries = entries.Where(e => e.At >= fromValue);
        }

        if (to is { } toValue)
        {
            entries = entries.Where(e => e.At < toValue);
        }

        var total = await entries.CountAsync(cancellationToken);
        if (query["cursor"].ToString() is { Length: > 0 } cursor)
        {
            if (CursorCodec.DecodeInstantId(cursor) is not var (at, rawId) || !Guid.TryParse(rawId, out var id))
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor).ToHttpResult();
            }

            entries = entries.Where(e => e.At < at || (e.At == at && e.Id.CompareTo(id) < 0));
        }

        var page = await entries.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(limit.Value + 1)
            .ToListAsync(cancellationToken);
        var next = page.Count > limit
            ? CursorCodec.EncodeInstantId(page[limit.Value - 1].At, page[limit.Value - 1].Id.ToString())
            : null;
        page = page.Take(limit.Value).ToList();
        var actorIds = page.Select(e => e.ActorId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        return TypedResults.Ok(new Page<AuditEntryResponse>(page.Select(e => new AuditEntryResponse(
            e.Id.ToString(), e.At, new AuditActor(e.ActorId, names.GetValueOrDefault(e.ActorId)), e.Service, e.Action,
            e.TargetType, e.TargetId, e.Changes, e.CorrelationId)).ToArray(), total, next));
    }

    private static bool TryInstant(string? value, out DateTimeOffset? instant)
    {
        instant = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            instant = parsed;
            return true;
        }

        return false;
    }

    private static bool ValidRoles(IReadOnlyList<string>? roles, bool allowEmpty) =>
        roles is not null && (allowEmpty || roles.Count > 0) && roles.Count <= StaffRoles.All.Count &&
        roles.All(StaffRoles.IsStaffRole);

    private static List<string> Normalize(IReadOnlyList<string> roles) =>
        StaffRoles.All.Where(role => roles.Contains(role, StringComparer.Ordinal)).ToList();

    internal static string ETagOf(User user) => Preconditions.ETagOf(
        user.DisplayName,
        string.Join(',', StaffMfaFlow.StaffRolesOf(user)),
        user.StaffDisabledAt?.ToString("O"),
        user.MfaEnrolledAt?.ToString("O"));

    internal static StaffMemberResponse ToMember(User user) => new(user.Id, user.DisplayName, user.Email,
        StaffMfaFlow.StaffRolesOf(user), user.StaffDisabledAt is not null, user.MfaEnrolledAt, user.LastStaffLoginAt,
        user.CreatedAt);

    internal static string AdminSetLockSql => AdminSetLock;
}

/// <summary>Personel kaydını kilitleyip değiştiren ortak adımlar (If-Match, kendini değiştirme, son yönetici).</summary>
public sealed class StaffAdminService(IdentityDbContext db, SessionService sessions, StaffAuditLog audit, TimeProvider clock)
{
    public async Task<IResult> UpdateRolesAsync(string id, List<string> roles, HttpContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> before = [];
        var result = await MutateAsync(id, context, "staff.roles-updated", forbidSelf: false, async (user, _) =>
        {
            before = StaffMfaFlow.StaffRolesOf(user);
            var actor = context.User.RequiredUserId();
            if (user.Id == actor && before.Contains(StaffRoles.Admin) && !roles.Contains(StaffRoles.Admin))
            {
                return ApiError.Conflict(ErrorCodes.CannotModifySelf);
            }

            if (before.Contains(StaffRoles.Admin) && !roles.Contains(StaffRoles.Admin) &&
                await OtherActiveAdminsAsync(user.Id, cancellationToken) == 0)
            {
                return ApiError.Conflict(ErrorCodes.LastAdmin);
            }

            user.Roles = [.. user.Roles.Where(r => !StaffRoles.IsStaffRole(r)), .. roles];
            return null;
        }, revokeSessions: true, cancellationToken, changes: () => new Dictionary<string, string?>
        {
            ["roles.from"] = string.Join(',', before),
            ["roles.to"] = string.Join(',', roles),
        });
        return result;
    }

    public Task<IResult> SetDisabledAsync(string id, bool disabled, HttpContext context, CancellationToken cancellationToken) =>
        MutateAsync(id, context, disabled ? "staff.disabled" : "staff.enabled", forbidSelf: true, async (user, _) =>
        {
            if (disabled && user.Roles.Contains(StaffRoles.Admin) && await OtherActiveAdminsAsync(user.Id, cancellationToken) == 0)
            {
                return ApiError.Conflict(ErrorCodes.LastAdmin);
            }

            user.StaffDisabledAt = disabled ? user.StaffDisabledAt ?? clock.GetUtcNow() : null;
            return null;
        }, revokeSessions: disabled, cancellationToken);

    /// <summary>
    /// Personel satırını kilitler, <c>If-Match</c>'i denetler, değişikliği uygular, denetim kaydı yazar
    /// ve (istenirse) personel oturumlarını sonlandırır. Yönetici kümesini değiştirebilecek işlemler
    /// danışma kilidiyle sıralanır (iki yönetici aynı anda birbirini düşüremez).
    /// </summary>
    public async Task<IResult> MutateAsync(string id, HttpContext context, string action, bool forbidSelf,
        Func<User, IdentityDbContext, Task<ApiError?>> mutate, bool revokeSessions, CancellationToken cancellationToken,
        bool requireIfMatch = true, Func<IReadOnlyDictionary<string, string?>>? changes = null)
    {
        var actor = context.User.RequiredUserId();
        if (forbidSelf && id == actor)
        {
            return ApiError.Conflict(ErrorCodes.CannotModifySelf).ToHttpResult();
        }

        if (requireIfMatch && !context.Request.HasIfMatch())
        {
            return ApiError.PreconditionRequired().ToHttpResult();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(StaffAdminEndpoints.AdminSetLockSql, cancellationToken);
        var user = await db.Users.FromSql($"SELECT * FROM users WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (user is null || !user.HasAnyRole(StaffRoles.All))
        {
            return ApiError.NotFound().ToHttpResult();
        }

        if (requireIfMatch && context.Request.CheckIfMatch(StaffAdminEndpoints.ETagOf(user)) is { } precondition)
        {
            return precondition.ToHttpResult();
        }

        if (await mutate(user, db) is { } error)
        {
            return error.ToHttpResult();
        }

        audit.Record(actor, action, "staff", user.Id, changes?.Invoke());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (revokeSessions)
        {
            await sessions.RevokeStaffSessionsAsync(user.Id, cancellationToken);
        }

        context.Response.SetETag(StaffAdminEndpoints.ETagOf(user));
        return TypedResults.Ok(StaffAdminEndpoints.ToMember(user));
    }

    private Task<int> OtherActiveAdminsAsync(string userId, CancellationToken cancellationToken) =>
        db.Users.CountAsync(u => u.Id != userId && u.Roles.Contains(StaffRoles.Admin) && u.StaffDisabledAt == null &&
                                 u.EmailVerified, cancellationToken);
}
