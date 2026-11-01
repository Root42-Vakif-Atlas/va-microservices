using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.Contracts;
using VakifAtlas.Media;
using VakifAtlas.Memories.Api.Infrastructure;
using VakifAtlas.Messaging;

namespace VakifAtlas.Memories.Api.Features;

/// <summary>Hesap silindi (KVKK): anılar, fotoğrafları, bildirimleri ve bekleyen yüklemeler silinir.</summary>
public sealed class AccountDeletedHandler(
    MemoriesDbContext db,
    PhotoUploadService<MemoriesDbContext> uploads,
    IObjectStorage storage,
    IOptions<StorageOptions> options) : IIntegrationEventHandler<AccountDeleted>
{
    public async Task HandleAsync(AccountDeleted integrationEvent, CancellationToken cancellationToken)
    {
        var userId = integrationEvent.UserId;
        var memories = await db.Memories.Where(m => m.AuthorId == userId).ToListAsync(cancellationToken);
        var reported = await db.Reports.Where(r => r.ReporterId == userId).Select(r => r.MemoryId).ToListAsync(cancellationToken);
        var memoryIds = memories.Select(m => m.Id).ToList();

        await db.Reports.Where(r => r.ReporterId == userId || memoryIds.Contains(r.MemoryId)).ExecuteDeleteAsync(cancellationToken);
        await MemoryEndpoints.RecountReportsAsync(db, reported.Except(memoryIds).ToList(), cancellationToken);
        db.Memories.RemoveRange(memories);

        var pending = await db.Set<PhotoUpload>().Where(u => u.OwnerId == userId && u.ConsumedAt == null).ToListAsync(cancellationToken);
        db.Set<PhotoUpload>().RemoveRange(pending);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var memory in memories)
        {
            await uploads.DeleteAsync(options.Value.Buckets.Public, MemoryEndpoints.Stored(memory), cancellationToken);
        }

        foreach (var upload in pending)
        {
            await storage.DeleteAsync(options.Value.Buckets.Uploads, upload.ObjectKey, cancellationToken);
        }
    }
}
