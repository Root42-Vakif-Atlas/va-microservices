using Microsoft.EntityFrameworkCore;
using VakifAtlas.Contracts;
using VakifAtlas.Gamification.Api.Infrastructure;
using VakifAtlas.Gamification.Domain;
using VakifAtlas.Messaging;

namespace VakifAtlas.Gamification.Api.Features;

/// <summary>
/// Katkı olayları deftere yazılır; XP okuma anında defterden hesaplanır (kurallar değişirse
/// geçmiş yeniden hesaplanır). Kimlik = olay kimliği: tekrar teslimat iki kez sayılmaz.
/// </summary>
public sealed class ContributionHandlers(GamificationDbContext db) :
    IIntegrationEventHandler<RouteCreated>,
    IIntegrationEventHandler<MemoryPublished>,
    IIntegrationEventHandler<DamageReportSubmitted>,
    IIntegrationEventHandler<AccountDeleted>
{
    public Task HandleAsync(RouteCreated integrationEvent, CancellationToken cancellationToken) =>
        RecordAsync(integrationEvent.EventId, integrationEvent.UserId, ContributionKind.RouteCreated, integrationEvent.RouteId,
            integrationEvent.Places, integrationEvent.OccurredAt, cancellationToken);

    public Task HandleAsync(MemoryPublished integrationEvent, CancellationToken cancellationToken) =>
        RecordAsync(integrationEvent.EventId, integrationEvent.UserId, ContributionKind.MemoryShared, integrationEvent.MemoryId,
            [integrationEvent.Place], integrationEvent.OccurredAt, cancellationToken);

    public Task HandleAsync(DamageReportSubmitted integrationEvent, CancellationToken cancellationToken) =>
        RecordAsync(integrationEvent.EventId, integrationEvent.UserId, ContributionKind.DamageReported, integrationEvent.ReportId,
            [integrationEvent.Place], integrationEvent.OccurredAt, cancellationToken);

    /// <summary>Hesap silinince oyunlaştırma kaydı silinir.</summary>
    public async Task HandleAsync(AccountDeleted integrationEvent, CancellationToken cancellationToken)
    {
        await db.Contributions.Where(c => c.UserId == integrationEvent.UserId).ExecuteDeleteAsync(cancellationToken);
        await db.Acknowledgements.Where(a => a.UserId == integrationEvent.UserId).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task RecordAsync(Guid eventId, string userId, ContributionKind kind, string referenceId,
        IEnumerable<ContributionPlace> places, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        if (await db.Contributions.AnyAsync(c => c.Id == eventId, cancellationToken))
        {
            return;
        }

        db.Contributions.Add(new Contribution
        {
            Id = eventId,
            UserId = userId,
            Kind = kind,
            ReferenceId = referenceId,
            Cities = places.Select(p => p.City).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            OccurredAt = occurredAt,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
