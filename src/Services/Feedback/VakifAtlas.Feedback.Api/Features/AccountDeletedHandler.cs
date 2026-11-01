using Microsoft.EntityFrameworkCore;
using VakifAtlas.Contracts;
using VakifAtlas.Feedback.Api.Infrastructure;
using VakifAtlas.Messaging;

namespace VakifAtlas.Feedback.Api.Features;

/// <summary>Geri bildirim ürün kaydıdır, silinmez: kişiyle bağı kaldırılır (idempotent).</summary>
public sealed class AccountDeletedHandler(FeedbackDbContext db) : IIntegrationEventHandler<AccountDeleted>
{
    public async Task HandleAsync(AccountDeleted integrationEvent, CancellationToken cancellationToken) =>
        await db.Entries.Where(f => f.UserId == integrationEvent.UserId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.UserId, (string?)null), cancellationToken);
}
