using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.Catalog.Api.Infrastructure.Import;
using VakifAtlas.Catalog.Api.Infrastructure.Persistence;
using VakifAtlas.Catalog.Domain.References;
using VakifAtlas.Media;

namespace VakifAtlas.Catalog.Api.Infrastructure.Mirroring;

public sealed class ImageMirrorOptions
{
    public const string Section = "Catalog:Images";

    // copy images added by URL into the institution's storage (until then they are served from the origin)
    public bool Mirror { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    public string UserAgent { get; set; } = "VakifAtlas/1.0 (https://vakifatlas.gov.tr; iletisim@vakifatlas.gov.tr)";
}

// wakes the mirror right after a staff save instead of waiting for the next interval
public sealed class ImageMirrorSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // already signalled
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);
}

// downloads images added by URL (media_assets.storage_key IS NULL), re-encodes them (metadata stripped, plus
// thumbnail) and stores them at catalog/_mirror/<hash of the URL>.jpg. Rows are leased with SKIP LOCKED, so
// replicas never fetch the same image and no transaction is held during downloads. A failure retries with
// exponential backoff up to MediaAsset.MaxMirrorAttempts; then the image stays on its origin and the error
// shows in the staff panel (saving the image again retries)
internal sealed class ImageMirrorService(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IOptions<ImageMirrorOptions> options,
    PhotoProcessor processor,
    ImageMirrorSignal signal,
    TimeProvider clock,
    ILogger<ImageMirrorService> logger) : BackgroundService
{
    public const string HttpClientName = "image-mirror";
    public const int BatchSize = 20;

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    private static readonly string[] ImageTypes = ["image/jpeg", "image/png", "image/webp"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Mirror)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // a full batch means more may be waiting
                while (await MirrorPendingAsync(stoppingToken) == BatchSize)
                {
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Image mirroring failed");
            }

            await signal.WaitAsync(options.Value.Interval, stoppingToken);
        }
    }

    // returns the number of rows leased
    internal async Task<int> MirrorPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var leased = await db.MediaAssets.FromSql($"""
            UPDATE media_assets SET mirror_retry_at = now() + {Lease}
            WHERE id IN (
              SELECT id FROM media_assets
              WHERE storage_key IS NULL AND mirror_attempts < {MediaAsset.MaxMirrorAttempts}
                AND (mirror_retry_at IS NULL OR mirror_retry_at <= now())
              ORDER BY mirror_attempts, id
              LIMIT {BatchSize}
              FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).ToListAsync(cancellationToken);
        if (leased.Count == 0)
        {
            return 0;
        }

        var http = httpClients.CreateClient(HttpClientName);
        var mirrored = new List<long>();
        foreach (var media in leased)
        {
            try
            {
                await MirrorAsync(http, media, cancellationToken);
                mirrored.Add(media.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                media.MirrorAttempts++;
                media.MirrorRetryAt = clock.GetUtcNow() + TimeSpan.FromMinutes(30 * Math.Pow(2, media.MirrorAttempts - 1));
                media.MirrorError = exception.Message.Length > 500 ? exception.Message[..500] : exception.Message;
                logger.LogInformation("Image {Media} not mirrored (attempt {Attempt}): {Reason}", media.Id, media.MirrorAttempts,
                    exception.Message);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var importer = scope.ServiceProvider.GetRequiredService<CatalogImporter>();
        if (mirrored.Count > 0)
        {
            // pages showing these images change (URL): new version and events
            var works = await db.Works.Where(w => w.Images.Any(i => mirrored.Contains(i.MediaId)) ||
                                                  w.Parts.Any(p => p.Images.Any(i => mirrored.Contains(i.MediaId))))
                .Select(w => w.Key).ToListAsync(cancellationToken);
            var complexes = await db.Complexes.Where(c => c.Images.Any(i => mirrored.Contains(i.MediaId)))
                .Select(c => c.Key).ToListAsync(cancellationToken);
            await importer.PublishChangesAsync(works, complexes, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (mirrored.Count > 0)
        {
            await importer.EvictCacheAsync(cancellationToken);
            logger.LogInformation("Mirrored {Count} images", mirrored.Count);
        }

        return leased.Count;
    }

    // object key from the origin URL: the same origin always maps to the same object (origin_url is unique)
    internal static string KeyOf(string originUrl) =>
        $"catalog/_mirror/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(originUrl)))[..32]}";

    private async Task MirrorAsync(HttpClient http, MediaAsset media, CancellationToken cancellationToken)
    {
        if (!SafeHttp.IsAllowedUrl(media.OriginUrl))
        {
            throw new InvalidDataException("URL not allowed.");
        }

        using var response = await http.GetAsync(media.OriginUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is null || !ImageTypes.Contains(contentType) || response.Content.Headers.ContentLength > MediaAsset.MaxSizeBytes)
        {
            throw new InvalidDataException($"Unexpected response ({contentType}, {response.Content.Headers.ContentLength} bytes).");
        }

        using var buffer = new MemoryStream();
        await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MediaAsset.MaxSizeBytes)
                {
                    throw new InvalidDataException("Image larger than 5 MB.");
                }

                buffer.Write(chunk, 0, read);
            }
        }

        buffer.Position = 0;
        var processed = await processor.ProcessAsync(buffer, MediaAsset.MaxSizeBytes, cancellationToken, allowPngAndWebp: true)
                        ?? throw new InvalidDataException("Not a readable image.");
        if (processed.Full.Length > MediaAsset.MaxSizeBytes)
        {
            throw new InvalidDataException("Re-encoded image larger than 5 MB.");
        }

        var key = KeyOf(media.OriginUrl!);
        const string cacheControl = "public, max-age=31536000, immutable";
        var bucket = storageOptions.Value.Buckets.Public;
        await using (var full = new MemoryStream(processed.Full))
        {
            await storage.PutAsync(bucket, $"{key}.jpg", full, "image/jpeg", cancellationToken, cacheControl);
        }

        await using (var thumbnail = new MemoryStream(processed.Thumbnail))
        {
            await storage.PutAsync(bucket, $"{key}-thumb.jpg", thumbnail, "image/jpeg", cancellationToken, cacheControl);
        }

        media.StorageKey = $"{key}.jpg";
        media.ThumbnailKey = $"{key}-thumb.jpg";
        media.MimeType = "image/jpeg";
        media.SizeBytes = processed.Full.Length;
        media.MirrorRetryAt = null;
        media.MirrorError = null;
    }
}
