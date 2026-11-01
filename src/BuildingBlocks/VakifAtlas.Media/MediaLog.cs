using Microsoft.Extensions.Logging;

namespace VakifAtlas.Media;

/// <summary>Cached log messages for object storage and photo uploads.</summary>
internal static class MediaLog
{
    private static readonly Action<ILogger, int, Exception?> _storageNotReady = LoggerMessage.Define<int>(
        LogLevel.Warning, new EventId(1, nameof(StorageNotReady)), "Object storage not ready (attempt {Attempt})");

    private static readonly Action<ILogger, string, string, Exception?> _deleteObjectFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(2, nameof(DeleteObjectFailed)),
            "Deleting object {Bucket}/{Key} failed");

    private static readonly Action<ILogger, Exception?> _uploadCleanupFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(3, nameof(UploadCleanupFailed)), "Upload cleanup failed");

    public static void StorageNotReady(this ILogger logger, int attempt) =>
        _storageNotReady(logger, attempt, null);

    public static void DeleteObjectFailed(this ILogger logger, Exception exception, string bucket, string key) =>
        _deleteObjectFailed(logger, bucket, key, exception);

    public static void UploadCleanupFailed(this ILogger logger, Exception exception) =>
        _uploadCleanupFailed(logger, exception);
}
