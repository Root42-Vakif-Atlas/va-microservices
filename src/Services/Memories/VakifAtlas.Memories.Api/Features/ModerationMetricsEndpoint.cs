using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.Memories.Api.Infrastructure;
using VakifAtlas.Memories.Domain;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Memories.Api.Features;

/// <summary>Fotoğraf eşikleri; <c>Enabled</c> false ise fotoğraflar skorlanmaz.</summary>
public sealed record MetricsImageThresholds(bool Enabled, double NsfwReviewFrom, double NsfwRejectFrom, double SimilarityReviewBelow,
    double? SimilarityRejectBelow, int SimilarityMinReferences);

/// <summary>Bağlam onay koşulu; <c>Enabled</c> false ise bağlam kuralı uygulanmaz.</summary>
public sealed record MetricsContextThresholds(bool Enabled, double ApproveFrom, double ReviewBelow);

public sealed record MetricsThresholds(double ApproveBelow, double RejectFrom, bool ReviewPhotos, MetricsImageThresholds Images,
    double SpamReviewFrom, double SpamRejectFrom, MetricsContextThresholds Context);

public sealed record MetricsTotals(int Shared, int Scored, int AutoApproved, int Review, int AutoRejected, int Unscored);

/// <summary>İnceleme bandındaki anılar için personel kararları ve kuyruk durumu.</summary>
public sealed record MetricsHumanReview(int Waiting, int? OldestWaitingMinutes, int Decided, int Approved, int Rejected,
    int? MedianDecisionMinutes);

/// <summary>Doğruluk vekilleri: personelin YZ kararını tersine çevirdiği durumlar.</summary>
public sealed record MetricsOverrides(int AutoRejectRestored, int AutoApproveRemoved);

/// <summary>Skor kovası: anıların bugünkü durumu (eşiklerin gözden geçirilmesi için).</summary>
public sealed record MetricsBin(double From, double To, int Total, int Approved, int Pending, int Rejected);

public sealed record MetricsDay(string Date, int AutoApproved, int Review, int AutoRejected);

public sealed record MetricsLatency(int? P50Ms, int? P95Ms);

public sealed record ModerationMetrics(
    string Mode,
    string? ModelVersion,
    int Days,
    MetricsThresholds Thresholds,
    MetricsTotals Totals,
    MetricsHumanReview HumanReview,
    MetricsOverrides Overrides,
    IReadOnlyList<MetricsBin> ScoreHistogram,
    IReadOnlyDictionary<string, int> Categories,
    MetricsLatency Latency,
    IReadOnlyList<MetricsDay> Daily);

/// <summary>
/// <c>GET memories/admin/moderation-metrics?days=7|30|90</c> (ADR 58): YZ moderasyonunun karar dağılımı,
/// insan incelemesi, YZ kararının geri alınma oranı, skor histogramı ve gecikme. Pencere: paylaşım zamanı.
/// </summary>
internal static class ModerationMetricsEndpoint
{
    private sealed record Row(string? Verdict, double? Score, string Status, string? ModeratedBy, DateTimeOffset CreatedAt,
        DateTimeOffset? ModeratedAt, int? LatencyMs, string? TopCategory, string? ModelVersion, DateTimeOffset? ScoredAt);

    public static async Task<IResult> Get(HttpContext context, MemoriesDbContext db, IOptions<ModerationOptions> options,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        var daysText = context.Request.Query["days"].ToString();
        var days = daysText.Length == 0 ? 30 : int.TryParse(daysText, out var parsed) ? parsed : 0;
        if (days is not (7 or 30 or 90))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var now = clock.GetUtcNow();
        var since = now.AddDays(-days);
        var rows = await db.Memories.AsNoTracking().Where(m => m.CreatedAt >= since)
            .Select(m => new Row(m.AiVerdict, m.AiScore, m.Status, m.ModeratedBy, m.CreatedAt, m.ModeratedAt, m.AiLatencyMs,
                m.AiTopCategory, m.AiModelVersion, m.AiScoredAt))
            .ToListAsync(cancellationToken);
        var waiting = await db.Memories.AsNoTracking().Where(m => m.Status == MemoryStatus.Pending)
            .Select(m => (DateTimeOffset?)m.CreatedAt).MinAsync(cancellationToken);
        var waitingCount = await db.Memories.CountAsync(m => m.Status == MemoryStatus.Pending, cancellationToken);

        var ai = options.Value.Ai;
        var scored = rows.Where(r => r.Score is not null).ToList();
        var reviewed = rows.Where(r => r.Verdict is AiVerdict.Review or AiVerdict.Unscored).ToList();
        var staffDecided = reviewed.Where(r => r.ModeratedBy is not null && r.ModeratedBy != ModerationPolicy.ActorId).ToList();

        var metrics = new ModerationMetrics(
            options.Value.Mode,
            scored.MaxBy(r => r.ScoredAt)?.ModelVersion,
            days,
            new MetricsThresholds(ai.ApproveBelow, ai.RejectFrom, ai.ReviewPhotos,
                new MetricsImageThresholds(ai.Images.Enabled, ai.Images.NsfwReviewFrom, ai.Images.NsfwRejectFrom,
                    ai.Images.SimilarityReviewBelow, ai.Images.SimilarityRejectBelow, ai.Images.SimilarityMinReferences),
                ai.SpamReviewFrom, ai.SpamRejectFrom,
                new MetricsContextThresholds(ai.Context.Enabled, ai.Context.ApproveFrom, ai.Context.ReviewBelow)),
            new MetricsTotals(rows.Count, scored.Count, rows.Count(r => r.Verdict == AiVerdict.Approve),
                rows.Count(r => r.Verdict == AiVerdict.Review), rows.Count(r => r.Verdict == AiVerdict.Reject),
                rows.Count(r => r.Verdict == AiVerdict.Unscored)),
            new MetricsHumanReview(waitingCount, waiting is null ? null : (int)(now - waiting.Value).TotalMinutes, staffDecided.Count,
                staffDecided.Count(r => r.Status == MemoryStatus.Approved), staffDecided.Count(r => r.Status == MemoryStatus.Rejected),
                Median(staffDecided.Select(r => (r.ModeratedAt!.Value - r.CreatedAt).TotalMinutes))),
            new MetricsOverrides(
                rows.Count(r => r.Verdict == AiVerdict.Reject && r.Status == MemoryStatus.Approved),
                rows.Count(r => r.Verdict == AiVerdict.Approve && r.Status == MemoryStatus.Rejected &&
                                r.ModeratedBy != ModerationPolicy.ActorId)),
            Histogram(scored),
            scored.Where(r => r.Verdict is AiVerdict.Review or AiVerdict.Reject && r.TopCategory is not null)
                .GroupBy(r => r.TopCategory!).OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            new MetricsLatency(Percentile(scored, 0.5), Percentile(scored, 0.95)),
            rows.Where(r => r.Verdict is not null).GroupBy(r => TurkeyTime.DateOf(r.CreatedAt)).OrderBy(g => g.Key)
                .Select(g => new MetricsDay(g.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    g.Count(r => r.Verdict == AiVerdict.Approve), g.Count(r => r.Verdict is AiVerdict.Review or AiVerdict.Unscored),
                    g.Count(r => r.Verdict == AiVerdict.Reject)))
                .ToArray());
        return TypedResults.Ok(metrics);
    }

    private static MetricsBin[] Histogram(List<Row> scored) =>
        Enumerable.Range(0, 10).Select(i =>
        {
            var from = i / 10d;
            var to = (i + 1) / 10d;
            var bin = scored.Where(r => r.Score >= from && (r.Score < to || (i == 9 && r.Score <= 1))).ToList();
            return new MetricsBin(from, to, bin.Count, bin.Count(r => r.Status == MemoryStatus.Approved),
                bin.Count(r => r.Status == MemoryStatus.Pending), bin.Count(r => r.Status == MemoryStatus.Rejected));
        }).ToArray();

    private static int? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : (int)Math.Round(sorted[sorted.Length / 2]);
    }

    private static int? Percentile(List<Row> scored, double p)
    {
        var latencies = scored.Where(r => r.LatencyMs is not null).Select(r => r.LatencyMs!.Value).Order().ToArray();
        return latencies.Length == 0 ? null : latencies[Math.Min(latencies.Length - 1, (int)Math.Ceiling(p * latencies.Length) - 1)];
    }
}
