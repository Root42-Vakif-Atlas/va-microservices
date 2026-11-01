using Microsoft.Extensions.Logging;

namespace VakifAtlas.Messaging;

/// <summary>Cached log messages for the outbox dispatcher and event consumer.</summary>
internal static class MessagingLog
{
    private static readonly Action<ILogger, Exception?> _consumerStartFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(ConsumerStartFailed)), "Event consumer could not start; retrying");

    private static readonly Action<ILogger, string, int, Exception?> _consuming = LoggerMessage.Define<string, int>(
        LogLevel.Information, new EventId(2, nameof(Consuming)), "Consuming {Queue} ({Count} subscriptions)");

    private static readonly Action<ILogger, string, string?, int, int, Exception?> _handlingFailed =
        LoggerMessage.Define<string, string?, int, int>(LogLevel.Error, new EventId(3, nameof(HandlingFailed)),
            "Handling {EventType} {MessageId} failed (attempt {Attempt}/{MaxAttempts})");

    private static readonly Action<ILogger, Exception?> _dispatchFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(4, nameof(DispatchFailed)), "Outbox dispatch failed; retrying");

    private static readonly Action<ILogger, string, Guid, int, Exception?> _publishFailed =
        LoggerMessage.Define<string, Guid, int>(LogLevel.Warning, new EventId(5, nameof(PublishFailed)),
            "Publishing {EventType} {EventId} failed (attempt {Attempt})");

    public static void ConsumerStartFailed(this ILogger logger, Exception exception) =>
        _consumerStartFailed(logger, exception);

    public static void Consuming(this ILogger logger, string queue, int count) =>
        _consuming(logger, queue, count, null);

    public static void HandlingFailed(this ILogger logger, Exception exception, string eventType, string? messageId,
        int attempt, int maxAttempts) =>
        _handlingFailed(logger, eventType, messageId, attempt, maxAttempts, exception);

    public static void DispatchFailed(this ILogger logger, Exception exception) =>
        _dispatchFailed(logger, exception);

    public static void PublishFailed(this ILogger logger, Exception exception, string eventType, Guid eventId,
        int attempt) =>
        _publishFailed(logger, eventType, eventId, attempt, exception);
}
