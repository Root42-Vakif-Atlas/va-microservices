using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.CivilProtection.Api.Infrastructure;
using VakifAtlas.Contracts;
using VakifAtlas.Media;
using VakifAtlas.Messaging;

namespace VakifAtlas.CivilProtection.Api.Features;

/// <summary>Raporlar kültür varlığı koruma kaydıdır, silinmez: kişiyle bağı kaldırılır; bekleyen yüklemeler silinir.</summary>
public sealed class AccountDeletedHandler(CivilProtectionDbContext db, IObjectStorage storage, IOptions<StorageOptions> options)
    : IIntegrationEventHandler<AccountDeleted>
{
    public async Task HandleAsync(AccountDeleted integrationEvent, CancellationToken cancellationToken)
    {
        await db.Reports.Where(r => r.OwnerId == integrationEvent.UserId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.OwnerId, (string?)null), cancellationToken);

        var pending = await db.Set<PhotoUpload>().Where(u => u.OwnerId == integrationEvent.UserId && u.ConsumedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var upload in pending)
        {
            await storage.DeleteAsync(options.Value.Buckets.Uploads, upload.ObjectKey, cancellationToken);
        }

        db.Set<PhotoUpload>().RemoveRange(pending);
        await db.SaveChangesAsync(cancellationToken);
    }
}
