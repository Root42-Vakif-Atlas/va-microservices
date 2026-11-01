using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VakifAtlas.Contracts;

namespace VakifAtlas.Messaging;

/// <summary>Adds events to the outbox, which the caller's <c>SaveChangesAsync</c> persists with the business data.</summary>
public interface IEventPublisher
{
    /// <summary>Queues the event in the outbox for <see cref="OutboxDispatcher{TDbContext}"/> to publish.</summary>
    /// <typeparam name="TEvent">Event type marked with <see cref="EventNameAttribute"/>.</typeparam>
    /// <param name="integrationEvent">Event to publish.</param>
    void Publish<TEvent>(TEvent integrationEvent) where TEvent : class, IIntegrationEvent;
}

internal sealed class OutboxEventPublisher<TDbContext>(TDbContext db) : IEventPublisher
    where TDbContext : DbContext
{
    public void Publish<TEvent>(TEvent integrationEvent) where TEvent : class, IIntegrationEvent =>
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = integrationEvent.EventId,
            Type = EventNames.Of<TEvent>(),
            Payload = JsonSerializer.Serialize(integrationEvent, MessagingJson.Options),
            OccurredAt = integrationEvent.OccurredAt,
            TraceParent = Activity.Current?.Id,
        });
}

internal static class MessagingJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ServiceDefaults.ServiceDefaultsExtensions.ConfigureJson(options);
        return options;
    }
}
