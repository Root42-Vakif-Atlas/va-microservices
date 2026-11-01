using VakifAtlas.Contracts;
using VakifAtlas.Identity.Api.Infrastructure;
using VakifAtlas.Identity.Domain;
using VakifAtlas.Messaging;
using VakifAtlas.ServiceDefaults.Http;

namespace VakifAtlas.Identity.Api.Features.Staff;

/// <summary>
/// Merkezi denetim kaydı. Identity'nin kendi personel işlemleri (giriş, personel yönetimi)
/// iş değişikliğiyle aynı <c>SaveChangesAsync</c>'te doğrudan yazılır; diğer servisler
/// <see cref="StaffActionRecorded"/> olayıyla gönderir.
/// </summary>
public sealed class StaffAuditLog(IdentityDbContext db, IHttpContextAccessor accessor, TimeProvider clock)
{
    public const string Service = "identity";

    public void Record(string actorId, string action, string targetType, string targetId,
        IReadOnlyDictionary<string, string?>? changes = null)
    {
        var now = clock.GetUtcNow();
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(now),
            At = now,
            ActorId = actorId,
            Service = Service,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Changes = changes is null ? [] : new Dictionary<string, string?>(changes),
            CorrelationId = accessor.HttpContext?.Items[CorrelationIdMiddleware.ItemKey] as string,
        });
    }
}

/// <summary>Diğer servislerin personel işlemlerini denetim kaydına ekler (idempotent: olay kimliği birincil anahtar).</summary>
public sealed class StaffActionRecordedHandler(IdentityDbContext db) : IIntegrationEventHandler<StaffActionRecorded>
{
    public async Task HandleAsync(StaffActionRecorded integrationEvent, CancellationToken cancellationToken)
    {
        if (await db.AuditEntries.FindAsync([integrationEvent.EventId], cancellationToken) is not null)
        {
            return;
        }

        db.AuditEntries.Add(new AuditEntry
        {
            Id = integrationEvent.EventId,
            At = integrationEvent.OccurredAt,
            ActorId = integrationEvent.ActorId,
            Service = integrationEvent.Service,
            Action = integrationEvent.Action,
            TargetType = integrationEvent.TargetType,
            TargetId = integrationEvent.TargetId,
            Changes = new Dictionary<string, string?>(integrationEvent.Changes),
            CorrelationId = integrationEvent.CorrelationId,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
