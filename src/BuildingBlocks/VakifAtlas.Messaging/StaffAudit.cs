using Microsoft.AspNetCore.Http;
using VakifAtlas.Contracts;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;

namespace VakifAtlas.Messaging;

/// <summary>Writes staff actions to the audit log through the outbox, so the caller's <c>SaveChangesAsync</c> persists them with the change.</summary>
/// <param name="events">Publisher that adds the audit event to the outbox.</param>
/// <param name="accessor">Accessor for the current request and its staff user.</param>
/// <param name="clock">Clock for the event time and id.</param>
/// <param name="service">Name of the service recording the action.</param>
public sealed class StaffAudit(IEventPublisher events, IHttpContextAccessor accessor, TimeProvider clock, string service)
{
    /// <summary>Records an action by the current staff user.</summary>
    /// <param name="action">Name of the action.</param>
    /// <param name="targetType">Type of the affected entity.</param>
    /// <param name="targetId">Id of the affected entity.</param>
    /// <param name="changes">Changed non-personal field values.</param>
    public void Record(string action, string targetType, string targetId,
        IReadOnlyDictionary<string, string?>? changes = null)
    {
        HttpContext context = accessor.HttpContext ?? throw new InvalidOperationException("Staff audit requires an HTTP request.");
        events.Publish(new StaffActionRecorded(
            Guid.CreateVersion7(clock.GetUtcNow()),
            clock.GetUtcNow(),
            context.User.RequiredUserId(),
            service,
            action,
            targetType,
            targetId,
            changes ?? new Dictionary<string, string?>(),
            context.Items[CorrelationIdMiddleware.ItemKey] as string));
    }
}
