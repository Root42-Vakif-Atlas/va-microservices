using Microsoft.EntityFrameworkCore;
using VakifAtlas.Contracts;
using VakifAtlas.Messaging;
using VakifAtlas.Routes.Api.Infrastructure;

namespace VakifAtlas.Routes.Api.Features;

/// <summary>
/// Hesap silinince hesaba ait rotalar ve kaydedilen eserler silinir (KVKK; oturum açıkken oluşturulanlar
/// ve hesaba taşınan misafir kayıtları hesabındır). İdempotenttir.
/// </summary>
public sealed class AccountDeletedHandler(RoutesDbContext db) : IIntegrationEventHandler<AccountDeleted>
{
    public async Task HandleAsync(AccountDeleted integrationEvent, CancellationToken cancellationToken)
    {
        await db.Routes.Where(r => r.UserId == integrationEvent.UserId).ExecuteDeleteAsync(cancellationToken);
        var owner = OwnerScope.AccountKey(integrationEvent.UserId);
        await db.SavedMonuments.Where(s => s.OwnerKey == owner).ExecuteDeleteAsync(cancellationToken);
    }
}
