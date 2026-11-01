using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.Media;
using VakifAtlas.Media.Api.Infrastructure;
using VakifAtlas.Media.Domain;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Media.Api.Features;

/// <summary>
/// The owner of exports: the account when the caller is signed in, otherwise the installation (ADR 21/33;
/// same rules as <c>OwnerScope</c> in the Routes service).
/// <list type="bullet">
/// <item>A guest must send <c>X-Installation-Id</c> (UUID v4); it is optional when signed in (older clients).</item>
/// <item>A bearer token that is sent but invalid yields <c>401</c>, so an account's archive never silently
/// falls back to the guest view.</item>
/// <item>On the first signed-in request that carries an installation identifier, the installation's guest
/// records move to the account; when the account quota (50) is exceeded, the oldest are deleted.</item>
/// <item>A guest only sees the unowned records of the installation; after signing out, the account's archive
/// is not visible on that device.</item>
/// </list>
/// </summary>
/// <param name="Installation">Installation identifier from the request header, if any.</param>
/// <param name="UserId">Signed-in account, or <c>null</c> for a guest.</param>
public sealed record ExportOwner(Guid? Installation, string? UserId)
{
    /// <summary>Request header carrying the installation identifier.</summary>
    public const string InstallationHeader = "X-Installation-Id";

    /// <summary>Restricts a query to the exports visible to this owner.</summary>
    /// <param name="exports">The unfiltered exports.</param>
    public IQueryable<RouteExport> Exports(IQueryable<RouteExport> exports) => UserId is null
        ? exports.Where(e => e.InstallationId == Installation && e.UserId == null)
        : exports.Where(e => e.UserId == UserId);

    /// <summary>
    /// Determines the owner from the request and claims the installation's guest exports when the caller is
    /// signed in and sent an installation identifier.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="db">The media database.</param>
    /// <param name="storage">Object storage, used when the quota trim deletes objects.</param>
    /// <param name="options">Storage settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The owner, or <c>401</c> for an invalid bearer token and <c>400</c> for a missing or malformed installation identifier.</returns>
    public static async Task<Result<ExportOwner>> ResolveAsync(HttpContext context, MediaDbContext db, IObjectStorage storage,
        IOptions<StorageOptions> options, CancellationToken cancellationToken)
    {
        var userId = context.OptionalUserId();
        if (userId is null && context.Request.Headers.Authorization.Count > 0)
        {
            return ApiError.Unauthorized();
        }

        Guid? installation = null;
        if (context.Request.Headers[InstallationHeader].ToString() is { Length: > 0 } header)
        {
            if (!Guid.TryParse(header, out var parsed) || parsed.Version != 4)
            {
                return ApiError.BadRequest(ErrorCodes.InstallationIdRequired);
            }

            installation = parsed;
        }

        if (userId is null && installation is null)
        {
            return ApiError.BadRequest(ErrorCodes.InstallationIdRequired);
        }

        var owner = new ExportOwner(installation, userId);
        if (userId is not null && installation is not null)
        {
            await owner.ClaimGuestExportsAsync(db, storage, options.Value.Buckets.Exports, cancellationToken);
        }

        return owner;
    }

    /// <summary>
    /// Enforces the per-owner quota by deleting the oldest active exports together with their objects.
    /// </summary>
    /// <param name="db">The media database.</param>
    /// <param name="storage">Object storage.</param>
    /// <param name="bucket">Name of the exports bucket.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task TrimToQuotaAsync(MediaDbContext db, IObjectStorage storage, string bucket,
        CancellationToken cancellationToken)
    {
        var overflow = await Exports(db.Exports).Where(e => e.Status == ExportStatus.Active)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Skip(RouteExportRules.MaxPerOwner).ToListAsync(cancellationToken);
        foreach (var old in overflow)
        {
            await storage.DeleteAsync(bucket, old.ObjectKey, cancellationToken);
        }

        var ids = overflow.Select(e => e.Id).ToList();
        await db.Exports.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Moves the installation's guest exports to the account. Idempotent: concurrent requests update the same
    /// rows again and the quota trim may run more than once.
    /// </summary>
    private async Task ClaimGuestExportsAsync(MediaDbContext db, IObjectStorage storage, string bucket,
        CancellationToken cancellationToken)
    {
        var claimed = await db.Exports.Where(e => e.InstallationId == Installation && e.UserId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.UserId, UserId), cancellationToken);
        if (claimed > 0)
        {
            await TrimToQuotaAsync(db, storage, bucket, cancellationToken);
        }
    }
}
