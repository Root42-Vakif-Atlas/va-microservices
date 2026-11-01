using VakifAtlas.Feedback.Api.Infrastructure;
using VakifAtlas.Feedback.Domain;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Feedback.Api.Features;

public sealed record SubmitFeedbackRequest(string? Category, int? Rating, string? Message, string? AppVersion, string? Platform);

public sealed record SubmittedFeedbackResponse(string Id, DateTimeOffset CreatedAt);

/// <summary>
/// <c>POST feedback</c> (api-contracts.md → Geri bildirim): giriş gerekmez; Bearer gönderilmişse geçerli
/// olmalıdır (aksi halde <c>401</c>, misafire düşmez). Saatte en çok 5 (kullanıcı, misafirde istemci adresi).
/// Metin loglanmaz.
/// </summary>
public static class FeedbackEndpoints
{
    public static RouteGroupBuilder MapFeedbackEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost(string.Empty, Submit);
        return group.AllowAnonymous();
    }

    private static async Task<IResult> Submit(SubmitFeedbackRequest request, HttpContext context, FeedbackDbContext db,
        IUsageLimiter limiter, TimeProvider clock, CancellationToken cancellationToken)
    {
        var userId = context.OptionalUserId();
        if (userId is null && context.Request.Headers.Authorization.Count > 0)
        {
            return ApiError.Unauthorized().ToHttpResult();
        }

        if (!FeedbackRules.IsValid(request.Category, request.Rating, request.Message, request.AppVersion, request.Platform))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidFeedback).ToHttpResult();
        }

        var limitKey = userId is not null
            ? $"feedback:user:{userId}"
            : $"feedback:ip:{context.Connection.RemoteIpAddress}";
        var quota = await limiter.TryConsumeAsync(limitKey, FeedbackRules.MaxPerHour, TimeSpan.FromHours(1),
            cancellationToken);
        if (!quota.Allowed)
        {
            return ApiError.TooManyRequests(quota.RetryAfterSeconds).ToHttpResult();
        }

        var entry = new FeedbackEntry
        {
            Id = Ids.New("fb"),
            Category = request.Category!,
            Rating = request.Rating,
            Message = request.Message?.Trim() ?? string.Empty,
            AppVersion = request.AppVersion,
            Platform = request.Platform,
            Language = RequestLanguage.Resolve(context.Request),
            UserId = userId,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Entries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"feedback/{entry.Id}", new SubmittedFeedbackResponse(entry.Id, entry.CreatedAt));
    }
}
