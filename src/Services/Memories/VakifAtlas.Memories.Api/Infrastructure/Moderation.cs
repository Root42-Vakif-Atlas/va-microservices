using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using VakifAtlas.Media;
using VakifAtlas.Memories.Domain;

namespace VakifAtlas.Memories.Api.Infrastructure;

public sealed class ModerationOptions
{
    public const string Section = "Memories:Moderation";

    /// <summary>
    /// <c>auto</c>: anı doğrudan yayınlanır (bildirimler VGM'ye düşer). <c>manual</c>: anı
    /// <c>pending</c> olarak VGM moderasyon kuyruğuna düşer, onaylanınca yayınlanır. <c>ai</c>: moderasyon
    /// servisi metni (zararlılık, spam, eserle bağlam) ve fotoğrafları skorlar; eşiklere göre otomatik onay / VGM
    /// incelemesi / otomatik kaldırma (ADR 58).
    /// </summary>
    public string Mode { get; set; } = "auto";

    public AiModerationOptions Ai { get; set; } = new();
}

/// <summary>Varsayılan metin eşikleri Moderation/calibration.json'dan (model sürümü 0.4-int8).</summary>
public sealed class AiModerationOptions
{
    public double ApproveBelow { get; set; } = 0.31;

    public double RejectFrom { get; set; } = 0.96;

    /// <summary>Spam kuralları iki seviyelidir: yalnızca iletişim bilgisi 0,60 (inceleme), kesin kalıp 0,97 (kaldırma).</summary>
    public double SpamReviewFrom { get; set; } = 0.5;

    public double SpamRejectFrom { get; set; } = 0.9;

    /// <summary>Açıkken fotoğraflı anı, skorlar temiz olsa da VGM incelemesine düşer (her fotoğrafa insan gözü).</summary>
    public bool ReviewPhotos { get; set; }

    /// <summary>Paylaşım isteğinde skorlama için toplam bekleme (yeniden deneme dahil).</summary>
    public int TimeoutSeconds { get; set; } = 3;

    /// <summary>Skorlanamayan anıların yeniden denenme aralığı ve penceresi (sonrası yalnızca VGM).</summary>
    public int RetryIntervalSeconds { get; set; } = 5;

    public int RetryWindowMinutes { get; set; } = 60;

    public AiImageModerationOptions Images { get; set; } = new();

    public AiContextModerationOptions Context { get; set; } = new();

    public AiThresholds Thresholds => new(ApproveBelow, RejectFrom, SpamReviewFrom, SpamRejectFrom);
}

/// <summary>
/// Metnin eserle bağlamı (otomatik onay koşulu). Varsayılan eşikler Moderation/calibration-context.json'dan
/// (vakifatlas-context 1.0: paraphrase-multilingual-MiniLM-L12-v2 INT8; max(eser merkezi, en yakın 5 anı prototipi)).
/// </summary>
public sealed class AiContextModerationOptions
{
    /// <summary>Kapalıyken bağlam onay koşulu değildir (yalnızca zararlılık, spam ve fotoğraf kuralları).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Kalibrasyon: ilgili anıların %80'i (hold-out %90) üstünde, konu dışının %2,6'sı (hold-out %5) üstünde.</summary>
    public double ApproveFrom { get; set; } = 0.38;

    /// <summary>Fotoğraf esere benzese de bunun altı incelemeye düşer; ilgili anıların %1,5'i altında.</summary>
    public double ReviewBelow { get; set; } = 0.2;

    public AiContextThresholds? Thresholds => Enabled ? new(ApproveFrom, ReviewBelow) : null;
}

/// <summary>
/// Fotoğraf moderasyonu (NSFW + eser benzerliği). Varsayılan eşikler Moderation/calibration-vision.json'dan
/// (vakifatlas-vision 1.0: Falconsai NSFW INT8, DINOv2-small merkez benzerliği).
/// </summary>
public sealed class AiImageModerationOptions
{
    /// <summary>Kapalıyken fotoğraflar gönderilmez; yalnızca metin skorlanır (<see cref="AiModerationOptions.ReviewPhotos"/>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Güvenli 3.744 görselde ≥ 0,5: %0,03, ≥ 0,9: %0.</summary>
    public double NsfwReviewFrom { get; set; } = 0.5;

    public double NsfwRejectFrom { get; set; } = 0.9;

    /// <summary>Aynı eserin envanter fotoğraflarının %4,8'i, ilgisiz fotoğrafların %93,5'i bu değerin altında.</summary>
    public double SimilarityReviewBelow { get; set; } = 0.11;

    /// <summary>Benzerlikle otomatik kaldırma; varsayılan kapalı (iç mekân/detay fotoğrafları da düşük skorlanır).</summary>
    public double? SimilarityRejectBelow { get; set; }

    public int SimilarityMinReferences { get; set; } = 3;

    public AiImageThresholds Thresholds => new(NsfwReviewFrom, NsfwRejectFrom, SimilarityReviewBelow, SimilarityRejectBelow,
        SimilarityMinReferences);
}

/// <summary>
/// Anı moderasyonu: paylaşımda anının ilk durumunu belirler (<see cref="Memory.Status"/> ve YZ alanları).
/// YZ modunda <c>MemoryPublished</c> yalnızca onaydan sonra yayınlanır (XP de onaya bağlıdır).
/// </summary>
public interface IModerationGateway
{
    Task ModerateAsync(Memory memory, CancellationToken cancellationToken);
}

internal sealed class AutoApproveModeration : IModerationGateway
{
    public Task ModerateAsync(Memory memory, CancellationToken cancellationToken)
    {
        memory.Status = MemoryStatus.Approved;
        return Task.CompletedTask;
    }
}

/// <summary>Önce VGM onayı: yeni anı moderasyon kuyruğunda bekler.</summary>
internal sealed class ManualModeration : IModerationGateway
{
    public Task ModerateAsync(Memory memory, CancellationToken cancellationToken)
    {
        memory.Status = MemoryStatus.Pending;
        return Task.CompletedTask;
    }
}

/// <summary>YZ moderasyonu: skorlanamazsa anı <c>unscored</c> bekler, <see cref="AiModerationWorker"/> yeniden dener.</summary>
internal sealed class AiModeration(AiScoring scoring, TimeProvider clock) : IModerationGateway
{
    public async Task ModerateAsync(Memory memory, CancellationToken cancellationToken)
    {
        memory.Status = MemoryStatus.Pending;
        if (await scoring.TryScoreAsync(memory, cancellationToken) is { } result)
        {
            memory.ApplyAi(result.Assessment, result.LatencyMs, clock.GetUtcNow());
        }
        else
        {
            memory.MarkUnscored();
        }
    }
}

public sealed record AiScoringResult(AiAssessment Assessment, int LatencyMs);

/// <summary>
/// Anı metni, fotoğraf betimleri ve fotoğrafların küçük resimlerini moderasyon servisine tek istekte gönderir ve eşik
/// politikasını uygular. Küçük resim (640 px) NSFW ve benzerlik modellerinin 224 px girdisine yeter.
/// </summary>
public sealed class AiScoring(
    IModerationClient client,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IOptions<ModerationOptions> options,
    ILogger<AiScoring> logger)
{
    public async Task<AiScoringResult?> TryScoreAsync(Memory memory, CancellationToken cancellationToken)
    {
        var texts = new List<ClassifyItem> { new("text", memory.Text) };
        texts.AddRange(memory.Photos.Where(p => !string.IsNullOrWhiteSpace(p.Description))
            .Select((p, i) => new ClassifyItem($"photo-{i}", p.Description!)));
        var ai = options.Value.Ai;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var (images, missing) = ai.Images.Enabled ? await ReadThumbnailsAsync(memory, cancellationToken) : ([], []);
            var response = await client.ModerateAsync(new ModerateRequest(memory.MonumentId, texts, images), cancellationToken);
            var scored = response.Images
                .Select(i => new AiImageScore(i.Id, i.Readable, i.Nsfw, i.Similarity, i.References))
                .Concat(missing.Select(id => new AiImageScore(id, false, null, null, 0)))
                .ToList();
            var assessment = ModerationPolicy.Evaluate(
                response.Texts.Select(r => new AiTextScore(r.Scores, r.LanguageSupported, r.Lexicon.Count > 0, r.Spam,
                    r.SpamSignals, r.Relevance)).ToList(), scored,
                ai.Thresholds, ai.Images.Thresholds, ai.Context.Thresholds, memory.Photos.Count > 0, ai.ReviewPhotos,
                response.Model.Version,
                response.Vision is null ? null : $"{response.Vision.Version}+{response.References?.Version ?? "none"}",
                response.Context is null ? null : $"{response.Context.Version}+{response.Context.Index}");
            return new AiScoringResult(assessment, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
                                              or JsonException or InvalidOperationException or StorageUnavailableException ||
                                          (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Metin ve görsel loglanmaz; yalnızca kimlik ve hata türü.
            logger.LogWarning("AI moderation unavailable for {MemoryId}: {Error}", memory.Id,
                (exception.InnerException ?? exception).GetType().Name);
            return null;
        }
    }

    /// <summary>Silinmiş küçük resim okunamaz sayılır (incelemeye düşer); depo hatası yeniden denenir.</summary>
    private async Task<(List<ModerateImage> Images, List<string> Missing)> ReadThumbnailsAsync(Memory memory,
        CancellationToken cancellationToken)
    {
        var images = new List<ModerateImage>();
        var missing = new List<string>();
        foreach (var photo in memory.Photos)
        {
            ObjectContent? content;
            try
            {
                content = await storage.GetAsync(storageOptions.Value.Buckets.Public, photo.ThumbnailKey, cancellationToken);
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
                images.Add(new ModerateImage(photo.Id, buffer.ToArray()));
            }
        }

        return (images, missing);
    }
}

internal sealed class StorageUnavailableException(Exception inner) : Exception("Object storage unavailable.", inner);

public sealed record ClassifyItem(string Id, string Text);

/// <summary>Fotoğrafın küçük resmi; JSON'da base64 olarak gider.</summary>
public sealed record ModerateImage(string Id, byte[] Data);

public sealed record ModerateRequest(string? MonumentId, IReadOnlyList<ClassifyItem> Texts, IReadOnlyList<ModerateImage> Images);

public sealed record ClassifyModel(string Name, string Version);

public sealed record VisionModel(string Name, string Version, string Nsfw, string Embedding);

public sealed record ReferencesModel(string Version, int Works, int Images);

public sealed record ContextModel(string Name, string Version, string Embedding, string Index, int Works, int Prototypes);

public sealed record ClassifyLexiconHit(string Category, double Severity);

/// <summary>Bir metnin skorları; <c>Spam</c>/<c>SpamSignals</c> eski servis sürümünde yoksa 0/boş, <c>Relevance</c> null.</summary>
public sealed record ClassifyResult(
    string Id,
    IReadOnlyDictionary<string, double> Scores,
    IReadOnlyList<ClassifyLexiconHit> Lexicon,
    string? Language,
    bool LanguageSupported,
    double Spam = 0,
    IReadOnlyList<string>? SpamSignals = null,
    double? Relevance = null,
    double? MonumentRelevance = null);

public sealed record ImageResult(string Id, bool Readable, double? Nsfw, double? Similarity, int References);

public sealed record ModerateResponse(
    ClassifyModel Model,
    VisionModel? Vision,
    ReferencesModel? References,
    IReadOnlyList<ClassifyResult> Texts,
    IReadOnlyList<ImageResult> Images,
    ContextModel? Context = null);

/// <summary>Moderasyon servisi (Python/FastAPI, yalnızca iç ağ): <c>POST /v1/moderate</c>.</summary>
public interface IModerationClient
{
    Task<ModerateResponse> ModerateAsync(ModerateRequest request, CancellationToken cancellationToken);
}

internal sealed class HttpModerationClient(HttpClient http) : IModerationClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ModerateResponse> ModerateAsync(ModerateRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("v1/moderate", request, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ModerateResponse>(Json, cancellationToken);
        if (body?.Model is null || body.Texts is null || body.Images is null || body.Texts.Count != request.Texts.Count ||
            body.Images.Count != request.Images.Count || (request.Images.Count > 0 && body.Vision is null))
        {
            throw new InvalidOperationException("Unexpected moderation response.");
        }

        return body;
    }
}

public static class ModerationExtensions
{
    public static WebApplicationBuilder AddMemoryModeration(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(ModerationOptions.Section);
        builder.Services.AddOptions<ModerationOptions>().Bind(section)
            .Validate(o => o.Mode is "auto" or "manual" or "ai", "Memories:Moderation:Mode must be auto, manual or ai.")
            .Validate(o => o.Mode != "ai" || o.Ai.Thresholds.IsValid,
                "AI thresholds must satisfy 0 < ApproveBelow < RejectFrom <= 1 and 0 < SpamReviewFrom <= SpamRejectFrom <= 1.")
            .Validate(o => o.Mode != "ai" || o.Ai.Context.Thresholds?.IsValid != false,
                "AI context thresholds must satisfy -1 < ReviewBelow <= ApproveFrom <= 1.")
            .Validate(o => o.Mode != "ai" || o.Ai.Images.Thresholds.IsValid,
                "AI image thresholds must satisfy 0 < NsfwReviewFrom <= NsfwRejectFrom <= 1, -1 < SimilarityRejectBelow < " +
                "SimilarityReviewBelow <= 1 and SimilarityMinReferences >= 1.")
            .ValidateOnStart();

        switch (section.GetValue("Mode", "auto"))
        {
            case "manual":
                builder.Services.AddSingleton<IModerationGateway, ManualModeration>();
                break;
            case "ai":
                var timeout = TimeSpan.FromSeconds(Math.Max(1, section.GetValue("Ai:TimeoutSeconds", 3)));
                builder.Services
                    .AddHttpClient<IModerationClient, HttpModerationClient>(http =>
                        http.BaseAddress = new Uri(builder.Configuration["Services:Moderation"] ?? "http://moderation:8080/"))
                    .AddStandardResilienceHandler(resilience =>
                    {
                        // Paylaşım isteği bekler: tek yeniden deneme, toplam süre sınırlı (sonrası unscored → işçi).
                        resilience.TotalRequestTimeout.Timeout = timeout;
                        resilience.AttemptTimeout.Timeout = timeout / 2;
                        resilience.Retry.MaxRetryAttempts = 1;
                        resilience.Retry.Delay = TimeSpan.FromMilliseconds(100);
                        resilience.CircuitBreaker.SamplingDuration = timeout * 2;
                    });
                // Tipli HttpClient tekil servise bağlanmaz (DNS/handler ömrü): istek/işçi kapsamında çözülür.
                builder.Services.AddTransient<AiScoring>();
                builder.Services.AddScoped<IModerationGateway, AiModeration>();
                builder.Services.AddHostedService<AiModerationWorker>();
                break;
            default:
                builder.Services.AddSingleton<IModerationGateway, AutoApproveModeration>();
                break;
        }

        return builder;
    }
}
