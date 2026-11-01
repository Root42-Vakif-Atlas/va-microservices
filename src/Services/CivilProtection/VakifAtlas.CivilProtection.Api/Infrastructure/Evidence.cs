using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using VakifAtlas.CivilProtection.Domain;
using VakifAtlas.Media;

namespace VakifAtlas.CivilProtection.Api.Infrastructure;

/// <summary>
/// Bildirimlerin kanıt değerlendirmesi (moderasyon servisi <c>POST /v1/assess-report</c>). Varsayılan eşikler
/// Moderation/calibration-evidence.json'dan (hasar metni, katalog kopyası) ve anı moderasyonunun eşiklerinden.
/// </summary>
public sealed class EvidenceOptions
{
    public const string Section = "CivilProtection:Evidence";

    /// <summary>Kapalıyken bildirimler skorlanmaz, panelde etiket görünmez.</summary>
    public bool Enabled { get; set; }

    /// <summary>Hasar olmayan metnin %3'ü (hold-out %23) üstünde: tek başına "güçlü" yapmaz.</summary>
    public double DamageSupportsFrom { get; set; } = 0.40;

    /// <summary>Kalibrasyondaki en düşük hasar bildiriminin 0,05 altı (hold-out'ta gerçek bildirimlerin %5'i altında).</summary>
    public double DamageUnderminesBelow { get; set; } = 0.34;

    /// <summary>Katalog görselinin küçük resmi %99 yakalanır; aynı eserin başka katalog fotoğrafı %0,7 eşiği geçer.</summary>
    public double CatalogCopyFrom { get; set; } = 0.94;

    public double CategoryMargin { get; set; } = 0.05;

    /// <summary>Destekleyen bildirim penceresi (gün) ve skorlanamayanların yeniden deneme penceresi (saat).</summary>
    public int CorroborationDays { get; set; } = 14;

    public int RetryIntervalSeconds { get; set; } = 5;

    public int RetryWindowHours { get; set; } = 24;

    /// <summary>İstek başına bekleme (vatandaş beklemez; arka planda).</summary>
    public int TimeoutSeconds { get; set; } = 10;

    public EvidenceThresholds Thresholds => new(DamageSupportsFrom, DamageUnderminesBelow, CatalogCopyFrom, CategoryMargin);
}

public sealed record AssessText(string Id, string Text);

/// <summary>Fotoğrafın küçük resmi; JSON'da base64.</summary>
public sealed record AssessImage(string Id, byte[] Data);

public sealed record AssessReportRequest(string MonumentId, IReadOnlyList<AssessText> Texts, IReadOnlyList<AssessImage> Images);

public sealed record AssessModel(string Version);

public sealed record AssessIndex(string Version, string? Index = null);

public sealed record AssessDamage(double Relevance, IReadOnlyDictionary<string, double> Categories);

public sealed record AssessTextResult(
    string Id,
    IReadOnlyDictionary<string, double> Scores,
    double Spam,
    IReadOnlyList<string>? SpamSignals,
    AssessDamage? Damage);

public sealed record AssessImageResult(string Id, bool Readable, double? Nsfw, double? Similarity, int References, double? CatalogCopy);

public sealed record AssessReportResponse(
    AssessModel Model,
    AssessIndex? Vision,
    AssessIndex? References,
    AssessIndex? Context,
    IReadOnlyList<AssessTextResult> Texts,
    IReadOnlyList<AssessImageResult> Images);

public interface IEvidenceClient
{
    Task<AssessReportResponse> AssessAsync(AssessReportRequest request, CancellationToken cancellationToken);
}

internal sealed class HttpEvidenceClient(HttpClient http) : IEvidenceClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AssessReportResponse> AssessAsync(AssessReportRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("v1/assess-report", request, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AssessReportResponse>(Json, cancellationToken);
        if (body?.Model is null || body.Texts is null || body.Images is null || body.Texts.Count != request.Texts.Count ||
            body.Images.Count != request.Images.Count)
        {
            throw new InvalidOperationException("Unexpected assessment response.");
        }

        return body;
    }
}

public sealed record EvidenceScore(EvidenceSignals Signals, string Version);

/// <summary>
/// Açıklama, fotoğraf betimleri ve fotoğrafların küçük resimleri (özel kova) tek istekte gönderilir. Metin ve görsel
/// loglanmaz; servis durumsuzdur.
/// </summary>
public sealed class EvidenceScoring(
    IEvidenceClient client,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    ILogger<EvidenceScoring> logger)
{
    public async Task<EvidenceScore?> TryScoreAsync(DamageReport report, CancellationToken cancellationToken)
    {
        var texts = new List<AssessText> { new("text", report.Description) };
        texts.AddRange(report.Photos.Where(p => !string.IsNullOrWhiteSpace(p.Description))
            .Select((p, i) => new AssessText($"photo-{i}", p.Description!)));
        try
        {
            var (images, missing) = await ReadThumbnailsAsync(report, cancellationToken);
            var response = await client.AssessAsync(new AssessReportRequest(report.MonumentId, texts, images), cancellationToken);
            var description = response.Texts[0];
            var signals = new EvidenceSignals(
                response.Texts.Max(t => t.Scores.Values.DefaultIfEmpty(0).Max()),
                response.Texts.Max(t => t.Spam),
                response.Texts.SelectMany(t => t.SpamSignals ?? []).Distinct(StringComparer.Ordinal).ToArray() is { Length: > 0 } spam
                    ? spam
                    : null,
                description.Damage?.Relevance,
                description.Damage?.Categories,
                response.Images.Select(i => new EvidencePhoto(i.Id, i.Readable, i.Nsfw, i.Similarity, i.References, i.CatalogCopy))
                    .Concat(missing.Select(id => new EvidencePhoto(id, false, null, null, 0, null)))
                    .ToArray());
            var version = string.Join('/', response.Model.Version,
                response.Vision is null ? "none" : $"{response.Vision.Version}+{response.References?.Version ?? "none"}",
                response.Context is null ? "none" : $"{response.Context.Version}+{response.Context.Index}");
            return new EvidenceScore(signals, version);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
                                              or JsonException or InvalidOperationException or StorageUnavailableException ||
                                          (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("Evidence assessment unavailable for {ReportId}: {Error}", report.Id,
                (exception.InnerException ?? exception).GetType().Name);
            return null;
        }
    }

    /// <summary>Silinmiş küçük resim okunamaz sayılır; depo hatası yeniden denenir.</summary>
    private async Task<(List<AssessImage> Images, List<string> Missing)> ReadThumbnailsAsync(DamageReport report,
        CancellationToken cancellationToken)
    {
        var images = new List<AssessImage>();
        var missing = new List<string>();
        foreach (var photo in report.Photos)
        {
            ObjectContent? content;
            try
            {
                content = await storage.GetAsync(storageOptions.Value.Buckets.Private, photo.ThumbnailKey, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new StorageUnavailableException(exception);
            }

            if (content is null)
            {
                missing.Add(photo.Id);
                continue;
            }

            await using (content)
            {
                using var buffer = new MemoryStream();
                await content.Body.CopyToAsync(buffer, cancellationToken);
                images.Add(new AssessImage(photo.Id, buffer.ToArray()));
            }
        }

        return (images, missing);
    }
}

internal sealed class StorageUnavailableException(Exception inner) : Exception("Object storage unavailable.", inner);

/// <summary>
/// Yeni bildirimleri skorlar ve etiketler (vatandaş beklemez). Skorlama satır kilidi dışında; yazım <c>FOR UPDATE SKIP
/// LOCKED</c> altında. Yeni etiketlenen bildirim aynı esere açık bildirimlerin "destekleyen bildirim" sayısını değiştirir:
/// onların etiketi saklı sinyallerinden yeniden hesaplanır. Pencere dışında kalan skorlanmamış bildirim etiketsiz kalır.
/// </summary>
internal sealed class EvidenceWorker(
    IServiceScopeFactory scopes,
    IOptions<EvidenceOptions> options,
    TimeProvider clock,
    ILogger<EvidenceWorker> logger) : BackgroundService
{
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.RetryIntervalSeconds)), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Evidence assessment failed");
            }
        }
    }

    /// <summary>Bir tur: en eski skorlanmamış bildirimler; etiketlenen sayısını döner.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CivilProtectionDbContext>();
        var scoring = scope.ServiceProvider.GetRequiredService<EvidenceScoring>();
        var since = clock.GetUtcNow() - TimeSpan.FromHours(options.Value.RetryWindowHours);
        var candidates = await db.Reports.AsNoTracking()
            .Where(r => r.EvidenceScoredAt == null && r.CreatedAt >= since)
            .OrderBy(r => r.CreatedAt).Take(BatchSize).ToListAsync(cancellationToken);

        var scored = 0;
        foreach (var candidate in candidates)
        {
            if (await scoring.TryScoreAsync(candidate, cancellationToken) is not { } score)
            {
                // Servis kapalı: sonraki tura kalır.
                break;
            }

            if (await ApplyAsync(db, candidate.Id, score, cancellationToken))
            {
                scored++;
            }
        }

        return scored;
    }

    private async Task<bool> ApplyAsync(CivilProtectionDbContext db, string id, EvidenceScore score, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var report = await db.Reports.FromSql($"SELECT * FROM damage_reports WHERE id = {id} FOR UPDATE SKIP LOCKED")
            .SingleOrDefaultAsync(cancellationToken);
        if (report is not { EvidenceScoredAt: null })
        {
            return false;
        }

        var corroborations = await Corroboration.CountAsync(db, report, settings.CorroborationDays, cancellationToken);
        report.ApplyEvidence(score.Signals,
            EvidencePolicy.Evaluate(score.Signals, report.LocationCheck, report.Category, report.Urgency, corroborations,
                settings.Thresholds),
            score.Version, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        // Aynı esere açık, etiketli bildirimler: destekleyen bildirim sayısı değişti.
        var window = TimeSpan.FromDays(settings.CorroborationDays);
        var neighbours = await db.Reports
            .Where(r => r.MonumentId == report.MonumentId && r.Id != report.Id && r.EvidenceSignals != null &&
                        r.CreatedAt >= report.CreatedAt - window && r.CreatedAt <= report.CreatedAt + window &&
                        (r.Status == ReportStatus.Received || r.Status == ReportStatus.InReview || r.Status == ReportStatus.Forwarded))
            .ToListAsync(cancellationToken);
        foreach (var neighbour in neighbours)
        {
            var count = await Corroboration.CountAsync(db, neighbour, settings.CorroborationDays, cancellationToken);
            neighbour.Reassess(EvidencePolicy.Evaluate(neighbour.EvidenceSignals!, neighbour.LocationCheck, neighbour.Category,
                neighbour.Urgency, count, settings.Thresholds));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}

/// <summary>
/// Destekleyen bildirim: aynı esere ±pencere içinde başka hesaplardan (silinmiş hesaplar tek sayılır), kapatılmamış
/// ve "şüpheli" etiketlenmemiş bildirimler. Hesap başına bir.
/// </summary>
internal static class Corroboration
{
    public static Task<int> CountAsync(CivilProtectionDbContext db, DamageReport report, int days, CancellationToken cancellationToken)
    {
        var window = TimeSpan.FromDays(days);
        return db.Reports.AsNoTracking()
            .Where(r => r.MonumentId == report.MonumentId && r.Id != report.Id &&
                        (r.OwnerId == null || r.OwnerId != report.OwnerId) &&
                        r.CreatedAt >= report.CreatedAt - window && r.CreatedAt <= report.CreatedAt + window &&
                        r.Status != ReportStatus.Closed && r.EvidenceLevel != EvidenceLevel.Suspicious)
            .Select(r => r.OwnerId).Distinct().CountAsync(cancellationToken);
    }
}

public static class EvidenceExtensions
{
    public static WebApplicationBuilder AddReportEvidence(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(EvidenceOptions.Section);
        builder.Services.AddOptions<EvidenceOptions>().Bind(section)
            .Validate(o => !o.Enabled || o.Thresholds.IsValid,
                "Evidence thresholds must satisfy DamageUnderminesBelow <= DamageSupportsFrom <= 1 and 0 < CatalogCopyFrom <= 1.")
            .Validate(o => !o.Enabled || (o.CorroborationDays >= 1 && o.RetryWindowHours >= 1),
                "CorroborationDays and RetryWindowHours must be at least 1.")
            .ValidateOnStart();
        if (!section.GetValue("Enabled", false))
        {
            return builder;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, section.GetValue("TimeoutSeconds", 10)));
        builder.Services
            .AddHttpClient<IEvidenceClient, HttpEvidenceClient>(http =>
                http.BaseAddress = new Uri(builder.Configuration["Services:Moderation"] ?? "http://moderation:8080/"))
            .AddStandardResilienceHandler(resilience =>
            {
                resilience.TotalRequestTimeout.Timeout = timeout;
                resilience.AttemptTimeout.Timeout = timeout / 2;
                resilience.Retry.MaxRetryAttempts = 1;
                resilience.CircuitBreaker.SamplingDuration = timeout * 2;
            });
        builder.Services.AddTransient<EvidenceScoring>();
        builder.Services.AddHostedService<EvidenceWorker>();
        return builder;
    }
}
