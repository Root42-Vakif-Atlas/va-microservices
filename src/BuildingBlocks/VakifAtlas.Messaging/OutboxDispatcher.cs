using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace VakifAtlas.Messaging;

/// <summary>Publishes outbox rows to RabbitMQ with at-least-once delivery; replicas share rows through <c>FOR UPDATE SKIP LOCKED</c>.</summary>
internal sealed class OutboxDispatcher<TDbContext>(
    IServiceScopeFactory scopes,
    RabbitMqConnection connection,
    OutboxSignal signal,
    IOptions<MessagingOptions> options,
    TimeProvider clock,
    ILogger<OutboxDispatcher<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    private static readonly Histogram<double> PublishLag = MessagingTelemetry.Meter.CreateHistogram<double>(
        "vakifatlas.outbox.publish_lag", unit: "ms", description: "Olay oluşumundan yayına kadar geçen süre");

    private static readonly Counter<long> PublishFailures =
        MessagingTelemetry.Meter.CreateCounter<long>("vakifatlas.outbox.publish_failures");

    private IChannel? _channel;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishBatchAsync(stoppingToken);
                await CleanupAsync(stoppingToken);
                if (published < options.Value.BatchSize)
                {
                    await signal.WaitAsync(options.Value.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.DispatchFailed(exception);
                _channel = null;
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        DateTimeOffset now = clock.GetUtcNow();
        List<OutboxMessage> messages = await db.Set<OutboxMessage>()
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE processed_at IS NULL AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                ORDER BY occurred_at
                LIMIT {options.Value.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        if (messages.Count == 0)
        {
            return 0;
        }

        IChannel channel = await ChannelAsync(cancellationToken);
        foreach (OutboxMessage? message in messages)
        {
            try
            {
                var properties = new BasicProperties
                {
                    MessageId = message.Id.ToString(),
                    Type = message.Type,
                    ContentType = "application/json",
                    DeliveryMode = DeliveryModes.Persistent,
                    Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
                    Headers = new Dictionary<string, object?>(),
                };
                if (message.TraceParent is not null)
                {
                    properties.Headers["traceparent"] = message.TraceParent;
                }

                await channel.BasicPublishAsync(options.Value.Exchange, message.Type, mandatory: false, properties,
                    Encoding.UTF8.GetBytes(message.Payload), cancellationToken);
                message.ProcessedAt = clock.GetUtcNow();
                PublishLag.Record((message.ProcessedAt.Value - message.OccurredAt).TotalMilliseconds,
                    new KeyValuePair<string, object?>("type", message.Type));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = exception.Message.Length > 2000 ? exception.Message[..2000] : exception.Message;
                message.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(300, Math.Pow(2, message.Attempts)));
                PublishFailures.Add(1, new KeyValuePair<string, object?>("type", message.Type));
                logger.PublishFailed(exception, message.Type, message.Id, message.Attempts);
                _channel = null;
                break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return messages.Count;
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (now - _lastCleanup < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastCleanup = now;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        DateTimeOffset threshold = now - options.Value.ProcessedRetention;
        await db.Set<OutboxMessage>().Where(m => m.ProcessedAt < threshold).ExecuteDeleteAsync(cancellationToken);
        await db.Set<InboxMessage>().Where(m => m.ProcessedAt < threshold - TimeSpan.FromDays(23))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        IConnection amqp = await connection.GetAsync(cancellationToken);
        _channel = await amqp.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await connection.DeclareTopologyAsync(_channel, cancellationToken);
        return _channel;
    }
}

internal static class MessagingTelemetry
{
    public static readonly ActivitySource Source = new("VakifAtlas.Messaging");

    public static readonly Meter Meter = new("VakifAtlas.Messaging");
}
