using Microsoft.EntityFrameworkCore;
using VakifAtlas.Routes.Api.Infrastructure;
using VakifAtlas.Routes.Domain;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Routes.Api.Features;

public sealed record SavedMonumentItem(string? MonumentId, DateTimeOffset? SavedAt);

public sealed record SavedMonumentsRequest(List<SavedMonumentItem>? Items);

public sealed record SavedMonumentResponse(string MonumentId, DateTimeOffset SavedAt);

public sealed record SavedMonumentList(IReadOnlyList<SavedMonumentResponse> Items);

/// <summary>
/// <c>saved-monuments</c> (api-contracts.md → Kaydedilen eserler): sahiplik <see cref="OwnerScope"/> ile
/// (oturum açıksa hesap, değilse kurulum). <c>PUT</c> sahibin kümesini tümüyle değiştirir (idempotent;
/// istemci çevrimdışı değişiklikleri tek istekte eşitler).
/// </summary>
public static class SavedMonumentEndpoints
{
    public static RouteGroupBuilder MapSavedMonumentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet(string.Empty, List);
        group.MapPut(string.Empty, Replace);
        return group.AllowAnonymous();
    }

    private static async Task<IResult> List(HttpContext context, RoutesDbContext db, CancellationToken cancellationToken)
    {
        var resolved = await OwnerScope.ResolveAsync(context, db, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        context.Response.Headers.CacheControl = "private, no-store";
        return TypedResults.Ok(await ReadAsync(db, resolved.Value.SavedOwnerKey, cancellationToken));
    }

    private static async Task<IResult> Replace(SavedMonumentsRequest body, HttpContext context, RoutesDbContext db,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        var resolved = await OwnerScope.ResolveAsync(context, db, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        if (body.Items is null || body.Items.Any(i => i.MonumentId is null || i.SavedAt is null))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidSavedMonuments).ToHttpResult();
        }

        var items = body.Items.Select(i => new SavedMonumentEntry(i.MonumentId!, i.SavedAt!.Value.ToUniversalTime())).ToArray();
        if (!SavedMonumentRules.IsValid(items, clock.GetUtcNow()))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidSavedMonuments).ToHttpResult();
        }

        var scope = resolved.Value;
        var owner = scope.SavedOwnerKey;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SavedMonuments.Where(s => s.OwnerKey == owner).ExecuteDeleteAsync(cancellationToken);
        db.SavedMonuments.AddRange(items.Select(i => new SavedMonument
        {
            OwnerKey = owner,
            InstallationId = scope.Installation,
            MonumentId = i.MonumentId,
            SavedAt = i.SavedAt,
        }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        context.Response.Headers.CacheControl = "private, no-store";
        return TypedResults.Ok(await ReadAsync(db, owner, cancellationToken));
    }

    /// <summary>En son kaydedilen önce.</summary>
    private static async Task<SavedMonumentList> ReadAsync(RoutesDbContext db, string owner,
        CancellationToken cancellationToken)
    {
        var saved = await db.SavedMonuments.AsNoTracking().Where(s => s.OwnerKey == owner)
            .OrderByDescending(s => s.SavedAt).ThenBy(s => s.MonumentId)
            .Select(s => new SavedMonumentResponse(s.MonumentId, s.SavedAt))
            .ToListAsync(cancellationToken);
        return new SavedMonumentList(saved);
    }
}
