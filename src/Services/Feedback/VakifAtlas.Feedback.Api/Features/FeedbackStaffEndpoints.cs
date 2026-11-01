using Microsoft.EntityFrameworkCore;
using VakifAtlas.Feedback.Api.Infrastructure;
using VakifAtlas.Feedback.Domain;
using VakifAtlas.Messaging;
using VakifAtlas.ServiceDefaults.Admin;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Feedback.Api.Features;

public sealed record StaffHandler(string Id, string? DisplayName);

public sealed record StaffFeedbackResponse(string Id, string Category, int? Rating, string Message, string? AppVersion,
    string? Platform, string Language, bool FromAccount, DateTimeOffset CreatedAt, string Status, string? StaffNote,
    StaffHandler? HandledBy, DateTimeOffset? HandledAt)
{
    public static StaffFeedbackResponse From(FeedbackEntry entry) => new(entry.Id, entry.Category, entry.Rating, entry.Message,
        entry.AppVersion, entry.Platform, entry.Language, entry.UserId is not null, entry.CreatedAt, entry.Status,
        entry.StaffNote, entry.HandledBy is null ? null : new StaffHandler(entry.HandledBy, entry.HandledByName),
        entry.HandledAt);

    public static string ETagOf(FeedbackEntry entry) =>
        Preconditions.ETagOf(entry.Status, entry.StaffNote, entry.HandledAt?.ToString("O"));
}

public sealed record FeedbackTriageRequest(string? Status, string? Note);

public sealed record FeedbackSummary(int Total, IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, int> ByCategory, double? AverageRating, int RatingCount);

/// <summary>
/// VGM paneli (<c>feedback/admin</c>, rol <c>moderator</c> veya <c>vgm_admin</c>): süzgeçli
/// liste, ayrıntı, triaj durumu (<c>If-Match</c>) ve özet. Metin loglanmaz.
/// </summary>
public static class FeedbackStaffEndpoints
{
    public static RouteGroupBuilder MapStaffEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet(string.Empty, List);
        group.MapGet("summary", Summary);
        group.MapGet("{id}", Get);
        group.MapPut("{id}/status", Triage);
        return group;
    }

    private static async Task<IResult> List(HttpContext context, FeedbackDbContext db, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        var limit = QueryParsing.Limit(query["limit"], 20, 100);
        var categories = QueryParsing.CommaList(query["category"]);
        var statuses = QueryParsing.CommaList(query["status"]);
        var ratings = QueryParsing.CommaList(query["rating"]);
        var platform = query["platform"].ToString();
        var search = query["q"].ToString().Trim();
        if (limit is null || categories.Any(c => !FeedbackCategory.All.Contains(c)) || statuses.Any(s => !FeedbackStatus.All.Contains(s)) ||
            ratings.Any(r => r is not ("1" or "2" or "3" or "4" or "5" or "none")) ||
            (platform.Length > 0 && !FeedbackRules.Platforms.Contains(platform)) || search.Length > 100)
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var entries = db.Entries.AsNoTracking();
        if (categories.Count > 0)
        {
            entries = entries.Where(f => categories.Contains(f.Category));
        }

        if (statuses.Count > 0)
        {
            entries = entries.Where(f => statuses.Contains(f.Status));
        }

        if (ratings.Count > 0)
        {
            var values = ratings.Where(r => r != "none").Select(int.Parse).ToArray();
            var unrated = ratings.Contains("none");
            entries = entries.Where(f => (f.Rating != null && values.Contains(f.Rating.Value)) || (unrated && f.Rating == null));
        }

        if (platform.Length > 0)
        {
            entries = entries.Where(f => f.Platform == platform);
        }

        if (search.Length > 0)
        {
            var pattern = $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            entries = entries.Where(f => EF.Functions.ILike(f.Message, pattern));
        }

        var total = await entries.CountAsync(cancellationToken);
        if (query["cursor"].ToString() is { Length: > 0 } cursor)
        {
            if (CursorCodec.DecodeInstantId(cursor) is not var (createdAt, id))
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor).ToHttpResult();
            }

            entries = entries.Where(f => f.CreatedAt < createdAt || (f.CreatedAt == createdAt && string.Compare(f.Id, id) < 0));
        }

        var items = await entries.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id).Take(limit.Value + 1)
            .ToListAsync(cancellationToken);
        var next = items.Count > limit
            ? CursorCodec.EncodeInstantId(items[limit.Value - 1].CreatedAt, items[limit.Value - 1].Id)
            : null;
        return TypedResults.Ok(new Page<StaffFeedbackResponse>(
            items.Take(limit.Value).Select(StaffFeedbackResponse.From).ToArray(), total, next));
    }

    private static async Task<IResult> Get(string id, HttpContext context, FeedbackDbContext db, CancellationToken cancellationToken)
    {
        var entry = await db.Entries.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (entry is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        context.Response.SetETag(StaffFeedbackResponse.ETagOf(entry));
        return TypedResults.Ok(StaffFeedbackResponse.From(entry));
    }

    private static async Task<IResult> Triage(string id, FeedbackTriageRequest request, HttpContext context,
        FeedbackDbContext db, StaffAudit audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (request.Status is null || !FeedbackStatus.All.Contains(request.Status) ||
            (request.Note?.Length ?? 0) > FeedbackRules.MaxStaffNote)
        {
            return ApiError.BadRequest(ErrorCodes.InvalidFeedback).ToHttpResult();
        }

        if (!context.Request.HasIfMatch())
        {
            return ApiError.PreconditionRequired().ToHttpResult();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entry = await db.Entries.FromSql($"SELECT * FROM feedback WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (entry is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        if (context.Request.CheckIfMatch(StaffFeedbackResponse.ETagOf(entry)) is { } precondition)
        {
            return precondition.ToHttpResult();
        }

        var previous = entry.Status;
        entry.Triage(request.Status, request.Note, context.User.RequiredUserId(), context.User.DisplayName(), clock.GetUtcNow());
        audit.Record("feedback.triaged", "feedback", entry.Id, new Dictionary<string, string?>
        {
            ["status.from"] = previous,
            ["status.to"] = entry.Status,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        context.Response.SetETag(StaffFeedbackResponse.ETagOf(entry));
        return TypedResults.Ok(StaffFeedbackResponse.From(entry));
    }

    private static async Task<IResult> Summary(FeedbackDbContext db, CancellationToken cancellationToken)
    {
        var byStatus = await db.Entries.AsNoTracking().GroupBy(f => f.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var byCategory = await db.Entries.AsNoTracking().GroupBy(f => f.Category)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var ratings = await db.Entries.AsNoTracking().Where(f => f.Rating != null)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Average = g.Average(f => (double)f.Rating!.Value) })
            .SingleOrDefaultAsync(cancellationToken);
        return TypedResults.Ok(new FeedbackSummary(
            byStatus.Values.Sum(),
            FeedbackStatus.All.ToDictionary(s => s, s => byStatus.GetValueOrDefault(s)),
            FeedbackCategory.All.Order(StringComparer.Ordinal).ToDictionary(c => c, c => byCategory.GetValueOrDefault(c)),
            ratings is null ? null : Math.Round(ratings.Average, 2),
            ratings?.Count ?? 0));
    }
}
