using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace VakifAtlas.Messaging;

/// <summary>RabbitMQ and outbox settings bound from the <c>Messaging</c> configuration section.</summary>
public sealed class MessagingOptions
{
    /// <summary>Configuration section name these options are bound from.</summary>
    public const string Section = "Messaging";

    /// <summary>When false, RabbitMQ is not used and events stay in the outbox, which suits tests.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>AMQP connection URI.</summary>
    public string Uri { get; set; } = "amqp://guest:guest@localhost:5672/";

    /// <summary>Topic exchange that events are published to.</summary>
    public string Exchange { get; set; } = "vakifatlas.events";

    /// <summary>Direct exchange that routes failed messages to service DLQs.</summary>
    public string DeadLetterExchange { get; set; } = "vakifatlas.dlx";

    /// <summary>Alternate exchange whose queue keeps events no queue was bound for, so they can be replayed.</summary>
    public string UnroutedExchange { get; set; } = "vakifatlas.unrouted";

    /// <summary>Maximum handling attempts before a message goes to the service DLQ.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>TTL of the retry queue, so a failed message waits without blocking the consumer.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Quorum <c>x-delivery-limit</c> that guards against crashing consumer loops.</summary>
    public int BrokerDeliveryLimit { get; set; } = 20;

    /// <summary>Unacknowledged messages a consumer may hold at once.</summary>
    public ushort Prefetch { get; set; } = 16;

    /// <summary>Longest wait between outbox polls when no signal arrives.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum outbox rows published per batch.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>How long published outbox rows are kept before cleanup.</summary>
    public TimeSpan ProcessedRetention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>Single AMQP connection per application with automatic recovery enabled.</summary>
/// <param name="options">Messaging settings.</param>
/// <param name="environment">Host environment whose application name labels the connection.</param>
public sealed class RabbitMqConnection(IOptions<MessagingOptions> options, IHostEnvironment environment) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    /// <summary>Returns the open connection, creating it on first use or after it closes.</summary>
    /// <param name="cancellationToken">Token that cancels connecting.</param>
    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.Value.Uri),
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                ClientProvidedName = environment.ApplicationName,
            };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Declares the event, dead letter and unrouted exchanges plus the unrouted queue.</summary>
    /// <param name="channel">Channel used for the declarations.</param>
    /// <param name="cancellationToken">Token that cancels the declarations.</param>
    public async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        MessagingOptions settings = options.Value;
        await channel.ExchangeDeclareAsync(settings.UnroutedExchange, ExchangeType.Fanout, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(settings.UnroutedExchange, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(settings.UnroutedExchange, settings.UnroutedExchange, string.Empty,
            cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(settings.Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["alternate-exchange"] = settings.UnroutedExchange },
            cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(options.Value.DeadLetterExchange, ExchangeType.Direct, durable: true,
            autoDelete: false, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _lock.Dispose();
    }
}

internal sealed class RabbitMqHealthCheck(RabbitMqConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var open = (await connection.GetAsync(cancellationToken)).IsOpen;
            return open ? HealthCheckResult.Healthy() : new HealthCheckResult(context.Registration.FailureStatus, "RabbitMQ connection closed");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "RabbitMQ unreachable", exception);
        }
    }
}
