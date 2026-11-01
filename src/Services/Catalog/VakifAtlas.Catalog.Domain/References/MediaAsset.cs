namespace VakifAtlas.Catalog.Domain.References;

// every image file is stored once here; other tables reference it. An image added by URL (OriginUrl) has no
// stored object until the mirror job copies it: StorageKey, MimeType and SizeBytes are set together
public class MediaAsset
{
    public const int MaxSizeBytes = 5 * 1024 * 1024;
    public const int MaxMirrorAttempts = 5;

    public long Id { get; set; }
    public string? StorageKey { get; set; }
    public string? ThumbnailKey { get; set; }
    public string? SourcePath { get; set; } // original path from the JSON
    public string? MimeType { get; set; }
    public int? SizeBytes { get; set; }

    // attribution; null = the institution's own image
    public string? Author { get; set; }
    public string? License { get; set; }
    public string? SourceName { get; set; }
    public string? SourceUrl { get; set; } // page credited, not the file

    // mirroring of an image added by URL
    public string? OriginUrl { get; set; }
    public short MirrorAttempts { get; set; }
    public DateTimeOffset? MirrorRetryAt { get; set; } // also the lease while a job holds it
    public string? MirrorError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsStored => StorageKey is not null;
}
