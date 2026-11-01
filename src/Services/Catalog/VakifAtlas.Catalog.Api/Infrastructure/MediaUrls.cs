using Microsoft.Extensions.Options;
using VakifAtlas.Catalog.Domain.References;
using VakifAtlas.Media;

namespace VakifAtlas.Catalog.Api.Infrastructure;

// the columns of media_assets a response needs
public sealed record MediaRef(
    string? StorageKey, string? ThumbnailKey, string? OriginUrl,
    string? Author, string? License, string? SourceName, string? SourceUrl)
{
    public static MediaRef Of(MediaAsset m) =>
        new(m.StorageKey, m.ThumbnailKey, m.OriginUrl, m.Author, m.License, m.SourceName, m.SourceUrl);
}

public sealed record Attribution(string Author, string License, string SourceName, string? SourceUrl);

// public URLs of media: the stored object once there is one, else (an image added by URL, not mirrored yet)
// the origin. Attribution falls back to the institution for its own images
public sealed class MediaUrls(IOptions<StorageOptions> storage)
{
    public const string OwnAuthor = "Vakıflar Genel Müdürlüğü";
    public const string OwnLicense = "VGM";

    public string Url(MediaRef media) =>
        media.StorageKey is { } key ? storage.Value.PublicUrl(key) : media.OriginUrl ?? string.Empty;

    public string ThumbnailUrl(MediaRef media) =>
        (media.ThumbnailKey ?? media.StorageKey) is { } key ? storage.Value.PublicUrl(key) : media.OriginUrl ?? string.Empty;

    public string Url(string storageKey) => storage.Value.PublicUrl(storageKey);

    public static Attribution AttributionOf(MediaRef media) => media.Author is null
        ? new Attribution(OwnAuthor, OwnLicense, OwnAuthor, null)
        : new Attribution(media.Author, media.License ?? string.Empty, media.SourceName ?? media.Author, media.SourceUrl);
}
