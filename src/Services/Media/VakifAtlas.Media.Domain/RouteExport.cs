using System.Text.RegularExpressions;

namespace VakifAtlas.Media.Domain;

/// <summary>Lifecycle state of a <see cref="RouteExport"/>.</summary>
public enum ExportStatus
{
    /// <summary>The upload ticket was issued but the file has not been verified yet.</summary>
    Pending,

    /// <summary>The upload was verified and the export is visible to its owner.</summary>
    Active,
}

/// <summary>
/// A route PDF export (the server side of ADR 33). Like routes, it is owned by the account
/// (<see cref="UserId"/>) when the caller is signed in, otherwise by the installation
/// (<see cref="InstallationId"/> with an empty <see cref="UserId"/>). The guest records of an installation
/// move to the account on the first signed-in request. The object key contains neither the user nor the
/// route name; the bucket is private and downloads go through short-lived presigned URLs.
/// </summary>
public sealed class RouteExport
{
    /// <summary>Export identifier, prefixed with <c>exp</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Owning account; <c>null</c> for a guest record.</summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Installation that created the export; <c>null</c> for records of older clients that do not send an
    /// installation identifier.
    /// </summary>
    public Guid? InstallationId { get; init; }

    /// <summary>Identifier of the exported route.</summary>
    public required string RouteId { get; init; }

    /// <summary>Display name of the exported route, trimmed.</summary>
    public required string RouteName { get; init; }

    /// <summary>File name offered to the user when downloading.</summary>
    public required string FileName { get; init; }

    /// <summary>Size of the PDF in bytes, as declared by the client and verified on completion.</summary>
    public long ByteSize { get; init; }

    /// <summary>Number of stops on the exported route.</summary>
    public int StopCount { get; init; }

    /// <summary>Lowercase hexadecimal SHA-256 of the PDF, verified on completion.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Key of the object in the exports bucket; independent of the owner.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public ExportStatus Status { get; set; } = ExportStatus.Pending;

    /// <summary>When the upload ticket was issued.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the upload was verified; <c>null</c> while <see cref="ExportStatus.Pending"/>.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Limits and validation rules for <see cref="RouteExport"/>.</summary>
public static partial class RouteExportRules
{
    /// <summary>Maximum PDF size in bytes.</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>Maximum number of active exports per owner; the oldest are removed beyond it.</summary>
    public const int MaxPerOwner = 50;

    /// <summary>
    /// Maximum number of incomplete (awaiting upload) exports per owner, so that issuing tickets without
    /// uploading cannot fill the storage.
    /// </summary>
    public const int MaxPendingPerOwner = 10;

    /// <summary>Maximum length of the file name.</summary>
    public const int MaxFileName = 120;

    /// <summary>Maximum length of the route name.</summary>
    public const int MaxRouteName = 60;

    /// <summary>Content type of an export.</summary>
    public const string ContentType = "application/pdf";

    /// <summary>Validity of the presigned upload URL.</summary>
    public static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Validity of the presigned download URL.</summary>
    public static readonly TimeSpan DownloadLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Validity of a shared link, which is the upper bound of an S3 SigV4 presigned URL (7 days). It cannot be
    /// revoked; deleting the export deletes the object, which makes the link stop working.
    /// </summary>
    public static readonly TimeSpan ShareLinkLifetime = TimeSpan.FromDays(7);

    /// <summary>Checks the client supplied metadata of a new export.</summary>
    /// <param name="routeId">Identifier of the route.</param>
    /// <param name="routeName">Display name of the route.</param>
    /// <param name="fileName">File name ending in <c>.pdf</c> without path or reserved characters.</param>
    /// <param name="byteSize">Declared size in bytes, between 1 and <see cref="MaxBytes"/>.</param>
    /// <param name="stopCount">Number of stops, between 2 and 10.</param>
    /// <param name="sha256">64-character hexadecimal SHA-256 of the file.</param>
    /// <returns><c>true</c> when every value is present and within its limits.</returns>
    public static bool IsValid(string? routeId, string? routeName, string? fileName, long? byteSize, int? stopCount, string? sha256) =>
        routeId is { Length: > 0 and <= 60 } &&
        routeName?.Trim() is { Length: > 0 and <= MaxRouteName } &&
        fileName is { Length: > 4 and <= MaxFileName } && SafeFileName().IsMatch(fileName) &&
        fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
        byteSize is > 0 and <= MaxBytes &&
        stopCount is >= 2 and <= 10 &&
        sha256 is not null && Sha256Hex().IsMatch(sha256);

    /// <summary>Checks whether the first bytes of a file carry the PDF signature (<c>%PDF-</c>).</summary>
    /// <param name="prefix">The leading bytes of the file.</param>
    public static bool HasPdfSignature(ReadOnlySpan<byte> prefix) => prefix.StartsWith("%PDF-"u8);

    [GeneratedRegex(@"^[^\\/:*?""<>|\x00-\x1F]+$")]
    private static partial Regex SafeFileName();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();
}
