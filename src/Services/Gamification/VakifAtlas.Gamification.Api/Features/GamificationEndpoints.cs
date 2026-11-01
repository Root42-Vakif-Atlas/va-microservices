using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using VakifAtlas.Gamification.Api.Infrastructure;
using VakifAtlas.Gamification.Domain;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Gamification.Api.Features;

public sealed record AcknowledgeRequest(List<string?>? Ids);

/// <summary><c>gamification/me</c>: istemci XP talep edemez; yalnızca okur ve kutlamayı görüldü işaretler.</summary>
public static class GamificationEndpoints
{
    private const int MaxAcknowledgements = 50;
    private const int MaxIdLength = 64;

    public static RouteGroupBuilder MapGamificationEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("me", Me);
        group.MapPost("me/acknowledgements", Acknowledge);
        return group.RequireAuthorization();
    }

    private static async Task<IResult> Me(HttpContext context, GamificationDbContext db, CancellationToken cancellationToken)
    {
        var userId = context.User.RequiredUserId();
        var contributions = await db.Contributions.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        var seen = await db.Acknowledgements.AsNoTracking().Where(a => a.UserId == userId).Select(a => a.ItemId)
            .ToListAsync(cancellationToken);
        context.Response.Headers.CacheControl = "private, no-store";
        return TypedResults.Ok(ProgressCalculator.Compute(contributions, seen.ToHashSet(StringComparer.Ordinal),
            RequestLanguage.Resolve(context.Request)));
    }

    private static async Task<IResult> Acknowledge(AcknowledgeRequest request, ClaimsPrincipal principal, GamificationDbContext db,
        CancellationToken cancellationToken)
    {
        if (request.Ids is not { Count: > 0 and <= MaxAcknowledgements } ids ||
            ids.Any(id => string.IsNullOrEmpty(id) || id.Length > MaxIdLength))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidAcknowledgement).ToHttpResult();
        }

        var userId = principal.RequiredUserId();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            // İdempotent.
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO acknowledgements (user_id, item_id) VALUES ({userId}, {id}) ON CONFLICT DO NOTHING", cancellationToken);
        }

        return TypedResults.NoContent();
    }
}
