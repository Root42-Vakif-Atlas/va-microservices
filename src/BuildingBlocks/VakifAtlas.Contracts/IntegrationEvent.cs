namespace VakifAtlas.Contracts;

/// <summary>Cross-service event whose <see cref="EventNameAttribute"/> is its RabbitMQ routing key, shaped <c>&lt;context&gt;.&lt;event&gt;.v&lt;n&gt;</c>.</summary>
public interface IIntegrationEvent
{
    /// <summary>Unique event id used for outbox and inbox deduplication.</summary>
    Guid EventId { get; }

    /// <summary>Time the event happened in the producing service.</summary>
    DateTimeOffset OccurredAt { get; }
}

/// <summary>Assigns the versioned routing key to an integration event type.</summary>
/// <param name="name">Routing key such as <c>catalog.monument-upserted.v1</c>.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventNameAttribute(string name) : Attribute
{
    /// <summary>Routing key of the event.</summary>
    public string Name { get; } = name;
}

/// <summary>Resolves routing keys from <see cref="EventNameAttribute"/>.</summary>
public static class EventNames
{
    /// <summary>Returns the routing key of the given event type or throws if it has none.</summary>
    /// <param name="type">Event type marked with <see cref="EventNameAttribute"/>.</param>
    public static string Of(Type type) =>
        (Attribute.GetCustomAttribute(type, typeof(EventNameAttribute)) as EventNameAttribute)?.Name
        ?? throw new InvalidOperationException($"{type.Name} has no [EventName].");

    /// <summary>Returns the routing key of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Event type marked with <see cref="EventNameAttribute"/>.</typeparam>
    public static string Of<T>() where T : IIntegrationEvent => Of(typeof(T));
}
