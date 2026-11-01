using Microsoft.EntityFrameworkCore;
using VakifAtlas.Identity.Api.Features.Auth;
using VakifAtlas.Identity.Api.Infrastructure;
using VakifAtlas.Identity.Domain;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Identity.Api.Features.Staff;

/// <summary>
/// MFA biletinin yaşam döngüsü. Tüm sayaç/tüketim adımları koşullu tek UPDATE'tir: eşzamanlı
/// istekler deneme sınırını aşamaz, aynı TOTP adımı veya kurtarma kodu iki kez kabul edilmez,
/// bilet tek oturum açar.
/// </summary>
public sealed class StaffMfaFlow(
    IdentityDbContext db,
    AuthWorkflow workflow,
    MfaSecretProtector protector,
    SecretTokens secrets,
    TokenService tokens,
    StaffAuditLog audit,
    TimeProvider clock)
{
    public byte[] Secret(string protectedSecret) => protector.Unprotect(protectedSecret);

    /// <summary>Bileti ve personeli doğrular (deneme hakkı harcamaz).</summary>
    public async Task<(ApiError? Error, MfaChallenge? Challenge, User? User)> FindAsync(string mfaToken,
        MfaChallengePurpose purpose, CancellationToken cancellationToken)
    {
        if (mfaToken.Length > 200)
        {
            return (ApiError.BadRequest(ErrorCodes.MfaExpired), null, null);
        }

        var now = clock.GetUtcNow();
        var hash = SecretTokens.HashRefreshToken(mfaToken);
        var challenge = await db.MfaChallenges.SingleOrDefaultAsync(c => c.TokenHash == hash, cancellationToken);
        if (challenge is null || challenge.Purpose != purpose || !challenge.IsUsable(now))
        {
            return (ApiError.BadRequest(ErrorCodes.MfaExpired), null, null);
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == challenge.UserId, cancellationToken);
        var enrolled = user?.MfaSecret is not null;
        if (user is null || !StaffAccess.IsAllowed(user) || enrolled != (purpose == MfaChallengePurpose.Verify))
        {
            return (ApiError.BadRequest(ErrorCodes.MfaExpired), null, null);
        }

        if (user.LockRemaining(now) is { } remaining)
        {
            return (ApiError.Locked((int)Math.Ceiling(remaining.TotalSeconds)), null, null);
        }

        return (null, challenge, user);
    }

    /// <summary><see cref="FindAsync"/> + bilet başına bir deneme hakkı ayırır.</summary>
    public async Task<(ApiError? Error, MfaChallenge? Challenge, User? User)> BeginAsync(string mfaToken,
        MfaChallengePurpose purpose, CancellationToken cancellationToken)
    {
        var found = await FindAsync(mfaToken, purpose, cancellationToken);
        if (found.Error is not null)
        {
            return found;
        }

        var now = clock.GetUtcNow();
        var reserved = await db.MfaChallenges
            .Where(c => c.Id == found.Challenge!.Id && c.ConsumedAt == null && c.ExpiresAt > now &&
                        c.Attempts < MfaChallenge.MaxAttempts)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Attempts, c => c.Attempts + 1), cancellationToken);
        return reserved == 1 ? found : (ApiError.BadRequest(ErrorCodes.MfaExpired), null, null);
    }

    /// <summary>TOTP kodu; kabul edilen adım hesaba atomik yazılır (tekrar kullanım engeli).</summary>
    public async Task<bool> AcceptTotpAsync(User user, byte[] secret, string code, CancellationToken cancellationToken)
    {
        if (code.Length > 20 || Totp.Match(secret, code, clock.GetUtcNow(), user.LastTotpStep) is not { } step)
        {
            return false;
        }

        var updated = await db.Users
            .Where(u => u.Id == user.Id && (u.LastTotpStep == null || u.LastTotpStep < step))
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.LastTotpStep, step), cancellationToken);
        return updated == 1;
    }

    public async Task<bool> AcceptRecoveryCodeAsync(User user, string recoveryCode, CancellationToken cancellationToken)
    {
        var normalized = RecoveryCodes.Normalize(recoveryCode.Length > 40 ? string.Empty : recoveryCode);
        if (normalized.Length == 0)
        {
            return false;
        }

        var hash = RecoveryCodes.Hash(secrets, user.Id, normalized);
        var now = clock.GetUtcNow();
        var used = await db.RecoveryCodes
            .Where(c => c.UserId == user.Id && c.CodeHash == hash && c.UsedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.UsedAt, now), cancellationToken);
        if (used == 1)
        {
            audit.Record(user.Id, "staff.recovery-code-used", "staff", user.Id);
        }

        return used == 1;
    }

    public Task RecordFailureAsync(User user, CancellationToken cancellationToken) =>
        workflow.RecordFailedLoginAsync(user.Id, cancellationToken);

    public async Task<IResult> RejectAsync(User user, CancellationToken cancellationToken)
    {
        await RecordFailureAsync(user, cancellationToken);
        return ApiError.BadRequest(ErrorCodes.InvalidMfaCode).ToHttpResult();
    }

    /// <summary>Bileti tüketir ve personel oturumunu açar (tek işlem).</summary>
    public async Task<IResult> CompleteAsync(MfaChallenge challenge, User user, IReadOnlyList<string>? recoveryCodes,
        DateTimeOffset now, CancellationToken cancellationToken, Func<Task>? beforeSave = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var consumed = await db.MfaChallenges
            .Where(c => c.Id == challenge.Id && c.ConsumedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.ConsumedAt, now), cancellationToken);
        if (consumed == 0)
        {
            return ApiError.BadRequest(ErrorCodes.MfaExpired).ToHttpResult();
        }

        if (beforeSave is not null)
        {
            await beforeSave();
        }

        user.ClearLock();
        user.LastStaffLoginAt = now;
        var pair = tokens.StartStaffSession(user);
        audit.Record(user.Id, recoveryCodes is null ? "staff.signed-in" : "staff.mfa-enrolled", "staff", user.Id);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(new StaffSessionResponse(pair.AccessToken, pair.RefreshToken, pair.ExpiresIn,
            await ToStaffUserAsync(db, user, cancellationToken), recoveryCodes));
    }

    public static async Task<StaffUserResponse> ToStaffUserAsync(IdentityDbContext db, User user,
        CancellationToken cancellationToken) =>
        new(user.Id, user.DisplayName, user.Email, StaffRolesOf(user), user.MfaEnrolledAt, user.LastStaffLoginAt,
            await db.RecoveryCodes.CountAsync(c => c.UserId == user.Id && c.UsedAt == null, cancellationToken));

    public static IReadOnlyList<string> StaffRolesOf(User user) =>
        StaffRoles.All.Where(role => user.Roles.Contains(role, StringComparer.Ordinal)).ToArray();
}
