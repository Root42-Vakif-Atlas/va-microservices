using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VakifAtlas.Contracts;

namespace VakifAtlas.Messaging;

/// <summary>Handler for one event type that the inbox runs only once per event.</summary>
/// <typeparam name="TEvent">Event type the handler consumes.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Handler of integration events, not a .NET event delegate.")]
public interface IIntegrationEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    /// <summary>Handles the event inside the consumer's database transaction.</summary>
    /// <param name="integrationEvent">Event to handle.</param>
    /// <param name="cancellationToken">Token that cancels handling.</param>
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}

internal sealed record Subscription(string EventName, Type EventType, Type HandlerType)
{
    public string Consumer => HandlerType.Name;
}

internal sealed class SubscriptionRegistry(string queue)
{
    public string Queue { get; } = queue;

    public List<Subscription> Subscriptions { get; } = [];
}

/// <summary>Consumes one quorum queue per service; failures go through the TTL retry queue, counted by <c>x-retry-count</c>, and land in the DLQ at the limit.</summary>
internal sealed class EventConsumerService<TDbContext>(
    SubscriptionRegistry registry,
    IServiceScopeFactory scopes,
    RabbitMqConnection connection,
    IOptions<MessagingOptions> options,
    TimeProvider clock,
    ILogger<EventConsumerService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    private static readonly Counter<long> Consumed =
        MessagingTelemetry.Meter.CreateCounter<long>("vakifatlas.events.consumed");

    private const string RetryHeader = "x-retry-count";

    private static readonly Counter<long> Failed =
        MessagingTelemetry.Meter.CreateCounter<long>("vakifatlas.events.failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (registry.Subscriptions.Count == 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.ConsumerStartFailed(exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        MessagingOptions settings = options.Value;
        IConnection amqp = await connection.GetAsync(stoppingToken);
        IChannel channel = await amqp.CreateChannelAsync(cancellationToken: stoppingToken);
        await connection.DeclareTopologyAsync(channel, stoppingToken);

        var deadLetterQueue = $"{registry.Queue}.dlq";
        await channel.QueueDeclareAsync(deadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: stoppingToken);
        await channel.QueueBindAsync(deadLetterQueue, settings.DeadLetterExchange, registry.Queue,
            cancellationToken: stoppingToken);

        await channel.QueueDeclareAsync(RetryQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-message-ttl"] = (long)settings.RetryDelay.TotalMilliseconds,
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = registry.Queue,
            },
            cancellationToken: stoppingToken);

        await channel.QueueDeclareAsync(registry.Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = settings.BrokerDeliveryLimit,
                ["x-dead-letter-exchange"] = settings.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = registry.Queue,
            },
            cancellationToken: stoppingToken);
        foreach (var name in registry.Subscriptions.Select(s => s.EventName).Distinct())
        {
            await channel.QueueBindAsync(registry.Queue, settings.Exchange, name, cancellationToken: stoppingToken);
        }

        await channel.BasicQosAsync(0, settings.Prefetch, global: false, stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => OnReceivedAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(registry.Queue, autoAck: false, consumer, stoppingToken);
        logger.Consuming(registry.Queue, registry.Subscriptions.Count);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private string RetryQueue => $"{registry.Queue}.retry";

    private async Task OnReceivedAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        // Retried messages arrive through the default exchange, so the event name is read from Type.
        var eventName = delivery.BasicProperties.Type ?? delivery.RoutingKey;
        ActivityContext parent = delivery.BasicProperties.Headers?.TryGetValue("traceparent", out var raw) == true && raw is byte[] bytes
            && ActivityContext.TryParse(Encoding.UTF8.GetString(bytes), null, out ActivityContext context)
                ? context
                : default;
        using Activity? activity = MessagingTelemetry.Source.StartActivity($"consume {eventName}", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", registry.Queue);
        activity?.SetTag("messaging.message.id", delivery.BasicProperties.MessageId);

        try
        {
            foreach (Subscription? subscription in registry.Subscriptions.Where(s => s.EventName == eventName))
            {
                await HandleAsync(subscription, delivery, stoppingToken);
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            Consumed.Add(1, new KeyValuePair<string, object?>("type", eventName));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            Failed.Add(1, new KeyValuePair<string, object?>("type", eventName));
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            var attempt = RetryCount(delivery) + 1;
            logger.HandlingFailed(exception, eventName, delivery.BasicProperties.MessageId, attempt, options.Value.MaxAttempts);
            if (attempt >= options.Value.MaxAttempts)
            {
                // Rejecting routes the message through the DLX into the service DLQ.
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
                return;
            }

            var properties = new BasicProperties(delivery.BasicProperties)
            {
                Type = eventName,
                Headers = new Dictionary<string, object?>(delivery.BasicProperties.Headers ?? new Dictionary<string, object?>())
                {
                    [RetryHeader] = attempt,
                },
            };
            await channel.BasicPublishAsync(string.Empty, RetryQueue, mandatory: false, properties, delivery.Body, stoppingToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
        }
    }

    private async Task HandleAsync(Subscription subscription, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        IIntegrationEvent integrationEvent = JsonSerializer.Deserialize(delivery.Body.Span, subscription.EventType, MessagingJson.Options)
            as IIntegrationEvent ?? throw new JsonException($"Empty {subscription.EventName} payload");

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        DbSet<InboxMessage> inbox = db.Set<InboxMessage>();
        if (await inbox.AnyAsync(m => m.MessageId == integrationEvent.EventId && m.Consumer == subscription.Consumer,
                cancellationToken))
        {
            return;
        }

        var handler = scope.ServiceProvider.GetRequiredService(subscription.HandlerType);
        MethodInfo method = typeof(IIntegrationEventHandler<>).MakeGenericType(subscription.EventType)
            .GetMethod(nameof(IIntegrationEventHandler<IIntegrationEvent>.HandleAsync))!;
        await (Task)method.Invoke(handler, [integrationEvent, cancellationToken])!;

        inbox.Add(new InboxMessage
        {
            MessageId = integrationEvent.EventId,
            Consumer = subscription.Consumer,
            ProcessedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static int RetryCount(BasicDeliverEventArgs delivery) =>
        delivery.BasicProperties.Headers?.TryGetValue(RetryHeader, out var value) == true && value is not null
            ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
}
