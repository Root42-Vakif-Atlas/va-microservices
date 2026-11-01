namespace VakifAtlas.Contracts;

/// <summary>Account was deleted under KVKK; memories are removed, reports anonymized, gamification and exports deleted.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the account was deleted.</param>
/// <param name="UserId">Id of the deleted user.</param>
[EventName("identity.account-deleted.v1")]
public sealed record AccountDeleted(Guid EventId, DateTimeOffset OccurredAt, string UserId) : IIntegrationEvent;

/// <summary>Monument was created or updated, so the Guide index and caches refresh.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the monument was saved.</param>
/// <param name="MonumentId">Id of the monument.</param>
/// <param name="CatalogVersion">Catalog version after the change.</param>
[EventName("catalog.monument-upserted.v1")]
public sealed record MonumentUpserted(Guid EventId, DateTimeOffset OccurredAt, string MonumentId, string CatalogVersion)
    : IIntegrationEvent;

/// <summary>Catalog version changed, which updates the <c>v</c> key of map tiles.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the version changed.</param>
/// <param name="Version">New catalog version.</param>
[EventName("catalog.version-changed.v1")]
public sealed record CatalogVersionChanged(Guid EventId, DateTimeOffset OccurredAt, string Version) : IIntegrationEvent;

/// <summary>Signed-in user created a route; guest routes are not published.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the route was created.</param>
/// <param name="UserId">Id of the route owner.</param>
/// <param name="RouteId">Id of the route.</param>
/// <param name="Places">Monuments on the route.</param>
[EventName("routes.route-created.v1")]
public sealed record RouteCreated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string UserId,
    string RouteId,
    IReadOnlyList<ContributionPlace> Places) : IIntegrationEvent;

/// <summary>Memory was published, after approval once moderation is in place.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the memory was published.</param>
/// <param name="UserId">Id of the author.</param>
/// <param name="MemoryId">Id of the memory.</param>
/// <param name="Place">Monument the memory belongs to.</param>
[EventName("memories.memory-published.v1")]
public sealed record MemoryPublished(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string UserId,
    string MemoryId,
    ContributionPlace Place) : IIntegrationEvent;

/// <summary>Damage report was submitted.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the report was submitted.</param>
/// <param name="UserId">Id of the reporter.</param>
/// <param name="ReportId">Id of the report.</param>
/// <param name="Place">Monument the report is about.</param>
[EventName("civil-protection.damage-report-submitted.v1")]
public sealed record DamageReportSubmitted(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string UserId,
    string ReportId,
    ContributionPlace Place) : IIntegrationEvent;

/// <summary>Monument and city of a contribution, used for the first contribution per city XP.</summary>
/// <param name="MonumentId">Id of the monument.</param>
/// <param name="City">City of the monument.</param>
public sealed record ContributionPlace(string MonumentId, string City);

/// <summary>Staff action for the central audit log, written to the outbox with the business change; <see cref="Changes"/> holds only non-personal values such as status or role.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the action was performed.</param>
/// <param name="ActorId">Id of the staff member.</param>
/// <param name="Service">Service that performed the action.</param>
/// <param name="Action">Name of the action.</param>
/// <param name="TargetType">Type of the affected entity.</param>
/// <param name="TargetId">Id of the affected entity.</param>
/// <param name="Changes">Changed field values without content text, emails or coordinates.</param>
/// <param name="CorrelationId">Correlation id of the originating request.</param>
[EventName("staff.action-recorded.v1")]
public sealed record StaffActionRecorded(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string ActorId,
    string Service,
    string Action,
    string TargetType,
    string TargetId,
    IReadOnlyDictionary<string, string?> Changes,
    string? CorrelationId) : IIntegrationEvent;

/// <summary>Staff changed the status of a damage report, consumed by the notification service.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the status changed.</param>
/// <param name="ReportId">Id of the report.</param>
/// <param name="OwnerId">Id of the reporter, or null when the report has no owner.</param>
/// <param name="Status">New status of the report.</param>
[EventName("civil-protection.damage-report-status-changed.v1")]
public sealed record DamageReportStatusChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string ReportId,
    string? OwnerId,
    string Status) : IIntegrationEvent;

/// <summary>Moderation decision was made for a memory.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the decision was made.</param>
/// <param name="MemoryId">Id of the memory.</param>
/// <param name="AuthorId">Id of the memory author.</param>
/// <param name="Decision">Moderation decision.</param>
[EventName("memories.memory-moderated.v1")]
public sealed record MemoryModerated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string MemoryId,
    string AuthorId,
    string Decision) : IIntegrationEvent;

/// <summary>Monument was archived or restored.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the publication state changed.</param>
/// <param name="MonumentId">Id of the monument.</param>
/// <param name="Publication">New publication state.</param>
/// <param name="CatalogVersion">Catalog version after the change.</param>
[EventName("catalog.monument-publication-changed.v1")]
public sealed record MonumentPublicationChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string MonumentId,
    string Publication,
    string CatalogVersion) : IIntegrationEvent;

/// <summary>Work was created or updated, so the Guide index and caches refresh.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the work was saved.</param>
/// <param name="WorkKey">Key of the work.</param>
/// <param name="CatalogVersion">Catalog version after the change.</param>
[EventName("catalog.work-upserted.v1")]
public sealed record WorkUpserted(Guid EventId, DateTimeOffset OccurredAt, string WorkKey, string CatalogVersion)
    : IIntegrationEvent;

/// <summary>Complex was created or updated, so the Guide index and caches refresh.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the complex was saved.</param>
/// <param name="ComplexKey">Key of the complex.</param>
/// <param name="CatalogVersion">Catalog version after the change.</param>
[EventName("catalog.complex-upserted.v1")]
public sealed record ComplexUpserted(Guid EventId, DateTimeOffset OccurredAt, string ComplexKey, string CatalogVersion)
    : IIntegrationEvent;

/// <summary>Work was archived or restored.</summary>
/// <param name="EventId">Unique event id.</param>
/// <param name="OccurredAt">Time the publication state changed.</param>
/// <param name="WorkKey">Key of the work.</param>
/// <param name="Publication">New publication state.</param>
/// <param name="CatalogVersion">Catalog version after the change.</param>
[EventName("catalog.work-publication-changed.v1")]
public sealed record WorkPublicationChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string WorkKey,
    string Publication,
    string CatalogVersion) : IIntegrationEvent;
