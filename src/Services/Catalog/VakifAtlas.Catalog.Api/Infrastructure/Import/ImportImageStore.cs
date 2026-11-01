using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using VakifAtlas.Catalog.Api.Infrastructure.Persistence;
using VakifAtlas.Catalog.Domain.References;
using VakifAtlas.Media;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Api.Infrastructure.Import;

public sealed record StoredImage(long MediaId, string Sha256, string Url, string ThumbnailUrl);

// content-addressed catalog images: the SHA-256 of the original file names the objects
// (catalog/_import/<sha>.jpg and <sha>-thumb.jpg) and the media_assets row, so a file is stored once
// however many works use it or however often it is imported
public sealed class ImportImageStore(
    CatalogDbContext db,
    IObjectStorage objects,
    PhotoProcessor processor,
    IOptions<StorageOptions> storage,
    MediaUrls urls)
{
    public const int MaxBytes = MediaAsset.MaxSizeBytes;

    private const string Prefix = "catalog/_import/";
    private const string Extension = ".jpg";
    private const string CacheControl = "public, max-age=31536000, immutable";

    public static string StorageKeyOf(string sha256) => Prefix + sha256 + Extension;

    public static string ThumbnailKeyOf(string sha256) => Prefix + sha256 + "-thumb" + Extension;

    // null for media not stored by the importer
    public static string? Sha256Of(string? storageKey)
    {
        if (storageKey is null || !storageKey.StartsWith(Prefix, StringComparison.Ordinal) || !storageKey.EndsWith(Extension, StringComparison.Ordinal))
        {
            return null;
        }

        var sha = storageKey[Prefix.Length..^Extension.Length];
        return VgmValidator.Sha256Pattern().IsMatch(sha) ? sha : null;
    }

    public static async Task<string> HashAsync(Stream content, CancellationToken cancellationToken) =>
        Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken));

    // sha256 → media id, for the ones already stored
    public async Task<Dictionary<string, long>> FindAsync(IEnumerable<string> sha256s, CancellationToken cancellationToken)
    {
        var keys = sha256s.Distinct(StringComparer.Ordinal).Select(StorageKeyOf).ToList();
        var rows = await db.MediaAssets.AsNoTracking().Where(m => keys.Contains(m.StorageKey!))
            .Select(m => new { m.Id, m.StorageKey }).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => Sha256Of(r.StorageKey)!, r => r.Id, StringComparer.Ordinal);
    }

    // verifies the hash, re-encodes (metadata stripped, ≤ processor size, plus thumbnail), writes both objects
    // and the media row. Storing a known file again returns the existing row (Created = false)
    public async Task<Result<(StoredImage Image, bool Created)>> StoreAsync(string sha256, Stream content, string? sourcePath,
        CancellationToken cancellationToken)
    {
        var key = StorageKeyOf(sha256);
        var known = await db.MediaAssets.AsNoTracking().Where(m => m.StorageKey == key).Select(m => (long?)m.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (known is { } knownId)
        {
            return (Stored(knownId, sha256), false);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return ApiError.BadRequest(ErrorCodes.PhotoTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        if (!Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)))
                .Equals(sha256, StringComparison.Ordinal))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidUpload);
        }

        buffer.Position = 0;
        var processed = await processor.ProcessAsync(buffer, MaxBytes, cancellationToken, allowPngAndWebp: true);
        if (processed is null || processed.Full.Length > MaxBytes)
        {
            return ApiError.BadRequest(ErrorCodes.InvalidUpload);
        }

        var bucket = storage.Value.Buckets.Public;
        await using (var full = new MemoryStream(processed.Full))
        {
            await objects.PutAsync(bucket, key, full, "image/jpeg", cancellationToken, CacheControl);
        }

        await using (var thumbnail = new MemoryStream(processed.Thumbnail))
        {
            await objects.PutAsync(bucket, ThumbnailKeyOf(sha256), thumbnail, "image/jpeg", cancellationToken, CacheControl);
        }

        var asset = new MediaAsset
        {
            StorageKey = key,
            ThumbnailKey = ThumbnailKeyOf(sha256),
            SourcePath = sourcePath is { Length: > CatalogLimits.Src } ? sourcePath[..CatalogLimits.Src] : sourcePath,
            MimeType = "image/jpeg",
            SizeBytes = processed.Full.Length,
        };
        db.MediaAssets.Add(asset);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // stored concurrently: the objects were written with the same content, the row exists
            db.Entry(asset).State = EntityState.Detached;
            var id = await db.MediaAssets.Where(m => m.StorageKey == key).Select(m => m.Id).SingleAsync(cancellationToken);
            return (Stored(id, sha256), false);
        }

        return (Stored(asset.Id, sha256), true);
    }

    private StoredImage Stored(long mediaId, string sha256) =>
        new(mediaId, sha256, urls.Url(StorageKeyOf(sha256)), urls.Url(ThumbnailKeyOf(sha256)));
}
