using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.CatalogClient;
using VakifAtlas.Contracts;
using VakifAtlas.Media;
using VakifAtlas.Memories.Api.Infrastructure;
using VakifAtlas.Memories.Domain;
using VakifAtlas.Messaging;
using VakifAtlas.ServiceDefaults.Admin;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Memories.Api.Features;

public sealed record StaffMonument(string Id, string Name, string City);

public sealed record StaffModerator(string Id, string? DisplayName);

/// <summary>Son karar; <c>DecidedBy</c> <c>ai</c> (YZ moderasyonu) veya <c>staff</c>.</summary>
public sealed record ModerationInfo(string? Reason, string? Note, StaffModerator? By, DateTimeOffset? At, string? DecidedBy);

/// <summary>Fotoğraf başına görsel skorlar: NSFW olasılığı ve eserle benzerlik (eserin referansı yoksa <c>null</c>).</summary>
public sealed record AiImageResponse(string PhotoId, bool Readable, double? Nsfw, double? Similarity, int References);

/// <summary>
/// YZ değerlendirmesi (ADR 58); <c>unscored</c>'da skor alanları boştur. <c>Score</c> metin kategorilerinin, spam kural
/// skorunun ve fotoğraf NSFW olasılığının en yükseği; <c>Images</c> fotoğraf başına ayrıntı (hangi fotoğraf işaretlendi);
/// <c>SpamSignals</c> eşleşen spam kuralları; <c>Relevance</c> metnin eserle bağlamı.
/// </summary>
public sealed record AiAssessmentResponse(
    string Verdict,
    double? Score,
    IReadOnlyDictionary<string, double>? Scores,
    string? TopCategory,
    string? ReviewCause,
    bool LexiconMatched,
    string? ModelVersion,
    DateTimeOffset? ScoredAt,
    double? Nsfw,
    double? Similarity,
    IReadOnlyList<AiImageResponse>? Images,
    string? VisionVersion,
    double? Spam,
    IReadOnlyList<string>? SpamSignals,
    double? Relevance,
    string? ContextVersion)
{
    public static AiAssessmentResponse? From(Memory memory) => memory.AiVerdict is null
        ? null
        : new AiAssessmentResponse(memory.AiVerdict, memory.AiScore, memory.AiScores, memory.AiTopCategory, memory.AiReviewCause,
            memory.AiLexiconMatched, memory.AiModelVersion, memory.AiScoredAt, memory.AiNsfw, memory.AiSimilarity,
            memory.AiImages?.Select(i => new AiImageResponse(i.PhotoId, i.Readable, i.Nsfw, i.Similarity, i.References)).ToArray(),
            memory.AiVisionVersion, memory.AiSpam, memory.AiSpamSignals, memory.AiRelevance, memory.AiContextVersion);
}

public sealed record StaffReportResponse(string Reason, string? Note, string Resolution, DateTimeOffset CreatedAt);

/// <summary>
/// VGM görünümü: herkese açık alanlara ek olarak açık bildirim sayısı ve nedenleri, moderasyon
/// bilgisi ve verilebilecek kararlar. Bildirenin kimliği gösterilmez (veri minimizasyonu).
/// </summary>
public sealed record StaffMemoryResponse(
    string Id,
    StaffMonument Monument,
    AuthorResponse Author,
    string Text,
    string? VisitedOn,
    IReadOnlyList<PhotoResponse> Photos,
    DateTimeOffset CreatedAt,
    string MemoryDate,
    string Status,
    bool PubliclyVisible,
    int OpenReports,
    IReadOnlyDictionary<string, int> ReportReasons,
    ModerationInfo Moderation,
    AiAssessmentResponse? Ai,
    IReadOnlyList<string> AllowedDecisions,
    IReadOnlyList<StaffReportResponse>? Reports)
{
    public static StaffMemoryResponse From(Memory memory, CatalogMonument? monument, IReadOnlyDictionary<string, int> reasons,
        IReadOnlyList<StaffReportResponse>? reports, StorageOptions storage)
    {
        var view = MemoryResponse.From(memory, storage);
        return new StaffMemoryResponse(memory.Id,
            new StaffMonument(memory.MonumentId, monument?.Name ?? memory.MonumentId, monument?.City ?? string.Empty),
            view.Author, memory.Text, view.VisitedOn, view.Photos, memory.CreatedAt, view.MemoryDate, memory.Status,
            memory.Status == MemoryStatus.Approved && memory.ReportCount < MemoryRules.HideAfterReports,
            memory.ReportCount, reasons,
            new ModerationInfo(memory.ModerationReason, memory.ModerationNote,
                memory.ModeratedBy is null ? null : new StaffModerator(memory.ModeratedBy, memory.ModeratedByName), memory.ModeratedAt,
                memory.ModeratedBy is null ? null : memory.DecidedByAi ? "ai" : "staff"),
            AiAssessmentResponse.From(memory), memory.AllowedDecisions(), reports);
    }

    /// <summary>YZ işçisi ile personel aynı anda karar verirse sonraki 412 alır (skorlama zamanı dahil).</summary>
    public static string ETagOf(Memory memory) => Preconditions.ETagOf(memory.Status,
        memory.ReportCount.ToString(CultureInfo.InvariantCulture), memory.ModeratedAt?.ToString("O"), memory.AiScoredAt?.ToString("O"));
}

public sealed record DecisionRequest(string? Decision, string? Reason, string? Note);

public sealed record ModerationSummary(int Pending, int Reported, int HiddenByReports, int Rejected, int Approved, int AutoRejected24h,
    int Unscored);

/// <summary>
/// Anı Köşesi moderasyonu (<c>memories/admin</c>, rol <c>moderator</c> veya <c>vgm_admin</c>):
/// bekleyen kuyruk, bildirilen (şikâyet) kuyruğu, ayrıntı, karar (<c>If-Match</c>) ve özet. Karar açık
/// bildirimleri sonuçlandırır (reddet → haklı, onayla → yersiz) ve denetim kaydına yazılır.
/// </summary>
public static class ModerationEndpoints
{
    public static RouteGroupBuilder MapModerationEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("memories", List);
        group.MapGet("memories/{id}", Get);
        group.MapPost("memories/{id}/decisions", Decide);
        group.MapGet("summary", Summary);
        group.MapGet("moderation-metrics", ModerationMetricsEndpoint.Get);
        return group;
    }

    private static async Task<IResult> List(HttpContext context, MemoriesDbContext db, ICatalogClient catalog,
        IOptions<StorageOptions> storage, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        var limit = QueryParsing.Limit(query["limit"], 20, 100);
        var statuses = QueryParsing.CommaList(query["status"]);
        var reported = query["reported"].ToString();
        var monumentId = query["monumentId"].ToString();
        var search = query["q"].ToString().Trim();
        var sort = query["sort"].ToString() is { Length: > 0 } value ? value : "newest";
        var verdicts = QueryParsing.CommaList(query["aiVerdict"]);
        var decidedBy = query["decidedBy"].ToString();
        if (limit is null || statuses.Any(s => !MemoryStatus.All.Contains(s)) || reported is not ("" or "true" or "false") ||
            monumentId.Length > 120 || search.Length > 100 || sort is not ("newest" or "oldest" or "mostReported" or "aiScore") ||
            verdicts.Any(v => !AiVerdict.All.Contains(v)) || decidedBy is not ("" or "ai" or "staff"))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var memories = db.Memories.AsNoTracking();
        if (statuses.Count > 0)
        {
            memories = memories.Where(m => statuses.Contains(m.Status));
        }

        memories = reported switch
        {
            "true" => memories.Where(m => m.ReportCount > 0),
            "false" => memories.Where(m => m.ReportCount == 0),
            _ => memories,
        };
        if (monumentId.Length > 0)
        {
            memories = memories.Where(m => m.MonumentId == monumentId);
        }

        if (verdicts.Count > 0)
        {
            memories = memories.Where(m => m.AiVerdict != null && verdicts.Contains(m.AiVerdict));
        }

        memories = decidedBy switch
        {
            "ai" => memories.Where(m => m.ModeratedBy == ModerationPolicy.ActorId),
            "staff" => memories.Where(m => m.ModeratedBy != null && m.ModeratedBy != ModerationPolicy.ActorId),
            _ => memories,
        };

        if (search.Length > 0)
        {
            var pattern = $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            memories = memories.Where(m => EF.Functions.ILike(m.Text, pattern) || EF.Functions.ILike(m.AuthorName, pattern));
        }

        var total = await memories.CountAsync(cancellationToken);
        if (query["cursor"].ToString() is { Length: > 0 } cursor)
        {
            if (CursorCodec.Decode(cursor, 3) is not [var keyText, var createdText, var id] ||
                !int.TryParse(keyText, CultureInfo.InvariantCulture, out var count) ||
                !CursorCodec.TryInstant(createdText, out var createdAt))
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor).ToHttpResult();
            }

            // aiScore: anahtar skorun on binde biri (skorsuz anılar -1, sona).
            var score = count / 10_000d;
            memories = sort switch
            {
                "aiScore" => memories.Where(m => (m.AiScore ?? -1) < score || ((m.AiScore ?? -1) == score && (m.CreatedAt < createdAt ||
                    (m.CreatedAt == createdAt && string.Compare(m.Id, id) < 0)))),
                "oldest" => memories.Where(m => m.CreatedAt > createdAt || (m.CreatedAt == createdAt && string.Compare(m.Id, id) > 0)),
                "mostReported" => memories.Where(m => m.ReportCount < count || (m.ReportCount == count && (m.CreatedAt < createdAt ||
                    (m.CreatedAt == createdAt && string.Compare(m.Id, id) < 0)))),
                _ => memories.Where(m => m.CreatedAt < createdAt || (m.CreatedAt == createdAt && string.Compare(m.Id, id) < 0)),
            };
        }

        memories = sort switch
        {
            "oldest" => memories.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id),
            "mostReported" => memories.OrderByDescending(m => m.ReportCount).ThenByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id),
            "aiScore" => memories.OrderByDescending(m => m.AiScore ?? -1).ThenByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id),
            _ => memories.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id),
        };
        var items = await memories.Take(limit.Value + 1).ToListAsync(cancellationToken);
        var last = items.Count > limit ? items[limit.Value - 1] : null;
        var next = last is null
            ? null
            : CursorCodec.Encode(
                (sort == "aiScore" ? (int)Math.Round((last.AiScore ?? -1) * 10_000) : last.ReportCount).ToString(CultureInfo.InvariantCulture),
                CursorCodec.Instant(last.CreatedAt), last.Id);
        var page = items.Take(limit.Value).ToList();
        var ids = page.Select(m => m.Id).ToList();
        var reasons = (await db.Reports.AsNoTracking()
                .Where(r => ids.Contains(r.MemoryId) && r.Resolution == ReportResolution.Open)
                .GroupBy(r => new { r.MemoryId, r.Reason })
                .Select(g => new { g.Key.MemoryId, g.Key.Reason, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.MemoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, int>)g.ToDictionary(x => x.Reason, x => x.Count));
        var monuments = await catalog.GetAsync(page.Select(m => m.MonumentId).Distinct(), RequestLanguage.Resolve(context.Request),
            cancellationToken);
        return TypedResults.Ok(new Page<StaffMemoryResponse>(page.Select(m => StaffMemoryResponse.From(m,
            monuments.GetValueOrDefault(m.MonumentId), reasons.GetValueOrDefault(m.Id) ?? new Dictionary<string, int>(), null,
            storage.Value)).ToArray(), total, next));
    }

    private static async Task<IResult> Get(string id, HttpContext context, MemoriesDbContext db, ICatalogClient catalog,
        IOptions<StorageOptions> storage, CancellationToken cancellationToken)
    {
        var memory = await db.Memories.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (memory is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        context.Response.SetETag(StaffMemoryResponse.ETagOf(memory));
        return TypedResults.Ok(await DetailAsync(memory, context, db, catalog, storage.Value, cancellationToken));
    }

    private static async Task<IResult> Decide(string id, DecisionRequest request, HttpContext context, MemoriesDbContext db,
        ICatalogClient catalog, IOptions<StorageOptions> storage, StaffAudit audit, IEventPublisher events, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var decision = request.Decision;
        if (decision is not (ModerationDecision.Approve or ModerationDecision.Reject) ||
            (decision == ModerationDecision.Reject && (request.Reason is null || !MemoryRules.ReportReasons.Contains(request.Reason))) ||
            (decision == ModerationDecision.Approve && request.Reason is not null) ||
            (request.Note?.Length ?? 0) > MemoryRules.MaxModerationNoteLength)
        {
            return ApiError.BadRequest(ErrorCodes.InvalidDecision).ToHttpResult();
        }

        if (!context.Request.HasIfMatch())
        {
            return ApiError.PreconditionRequired().ToHttpResult();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var memory = await db.Memories.FromSql($"SELECT * FROM memories WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (memory is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        if (context.Request.CheckIfMatch(StaffMemoryResponse.ETagOf(memory)) is { } precondition)
        {
            return precondition.ToHttpResult();
        }

        if (!memory.AllowedDecisions().Contains(decision))
        {
            return ApiError.Conflict(ErrorCodes.InvalidTransition).ToHttpResult();
        }

        var previous = memory.Status;
        var openReports = memory.ReportCount;
        var now = clock.GetUtcNow();
        var firstPublication = memory.Decide(decision, request.Reason, request.Note, context.User.RequiredUserId(),
            context.User.DisplayName(), now);
        var resolution = decision == ModerationDecision.Reject ? ReportResolution.Upheld : ReportResolution.Dismissed;
        await db.Reports.Where(r => r.MemoryId == id && r.Resolution == ReportResolution.Open)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Resolution, resolution).SetProperty(r => r.ResolvedAt, now),
                cancellationToken);

        if (firstPublication)
        {
            var monument = (await catalog.GetAsync([memory.MonumentId], "tr", cancellationToken)).GetValueOrDefault(memory.MonumentId);
            events.Publish(new MemoryPublished(Guid.CreateVersion7(now), now, memory.AuthorId, memory.Id,
                new ContributionPlace(memory.MonumentId, monument?.City ?? string.Empty)));
        }

        events.Publish(new MemoryModerated(Guid.CreateVersion7(now), now, memory.Id, memory.AuthorId, decision));
        audit.Record(decision == ModerationDecision.Approve ? "memory.approved" : "memory.rejected", "memory", memory.Id, new Dictionary<string, string?>
        {
            ["status.from"] = previous,
            ["status.to"] = memory.Status,
            ["reason"] = request.Reason,
            ["reports.resolved"] = openReports.ToString(CultureInfo.InvariantCulture),
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        context.Response.SetETag(StaffMemoryResponse.ETagOf(memory));
        return TypedResults.Ok(await DetailAsync(memory, context, db, catalog, storage.Value, cancellationToken));
    }

    private static async Task<IResult> Summary(MemoriesDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var byStatus = await db.Memories.AsNoTracking().GroupBy(m => m.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var reported = await db.Memories.CountAsync(m => m.ReportCount > 0 && m.Status != MemoryStatus.Rejected, cancellationToken);
        var hidden = await db.Memories.CountAsync(m => m.ReportCount >= MemoryRules.HideAfterReports && m.Status == MemoryStatus.Approved,
            cancellationToken);
        var dayAgo = clock.GetUtcNow().AddHours(-24);
        var autoRejected = await db.Memories.CountAsync(m => m.AiVerdict == AiVerdict.Reject && m.CreatedAt >= dayAgo, cancellationToken);
        var unscored = await db.Memories.CountAsync(m => m.AiVerdict == AiVerdict.Unscored && m.Status == MemoryStatus.Pending,
            cancellationToken);
        return TypedResults.Ok(new ModerationSummary(byStatus.GetValueOrDefault(MemoryStatus.Pending), reported, hidden,
            byStatus.GetValueOrDefault(MemoryStatus.Rejected), byStatus.GetValueOrDefault(MemoryStatus.Approved), autoRejected, unscored));
    }

    private static async Task<StaffMemoryResponse> DetailAsync(Memory memory, HttpContext context, MemoriesDbContext db,
        ICatalogClient catalog, StorageOptions storage, CancellationToken cancellationToken)
    {
        var reports = await db.Reports.AsNoTracking().Where(r => r.MemoryId == memory.Id).OrderByDescending(r => r.CreatedAt)
            .Take(200).ToListAsync(cancellationToken);
        var reasons = reports.Where(r => r.Resolution == ReportResolution.Open).GroupBy(r => r.Reason)
            .ToDictionary(g => g.Key, g => g.Count());
        var monument = (await catalog.GetAsync([memory.MonumentId], RequestLanguage.Resolve(context.Request), cancellationToken))
            .GetValueOrDefault(memory.MonumentId);
        return StaffMemoryResponse.From(memory, monument, reasons,
            reports.Select(r => new StaffReportResponse(r.Reason, r.Note, r.Resolution, r.CreatedAt)).ToArray(), storage);
    }
}
