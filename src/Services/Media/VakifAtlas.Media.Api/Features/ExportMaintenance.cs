using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.Contracts;
using VakifAtlas.Media;
using VakifAtlas.Media.Api.Infrastructure;
using VakifAtlas.Media.Domain;
using VakifAtlas.Messaging;

namespace VakifAtlas.Media.Api.Features;

/// <summary>
/// Deletes every export of a deleted account, including guest records that were moved to the account, together
/// with their objects. Idempotent: on redelivery there is nothing left to delete.
/// </summary>
public sealed class AccountDeletedHandler(MediaDbContext db, IObjectStorage storage, IOptions<StorageOptions> options)
    : IIntegrationEventHandler<AccountDeleted>
{
    /// <inheritdoc />
    public async Task HandleAsync(AccountDeleted integrationEvent, CancellationToken cancellationToken)
    {
        var bucket = options.Value.Buckets.Exports;
        var keys = await db.Exports.Where(e => e.UserId == integrationEvent.UserId).Select(e => e.ObjectKey)
            .ToListAsync(cancellationToken);
        foreach (var key in keys)
        {
            await storage.DeleteAsync(bucket, key, cancellationToken);
        }

        // Object keys used to be prefixed with the user (`<user>/…`).
        await storage.DeletePrefixAsync(bucket, $"{integrationEvent.UserId}/", cancellationToken);
        await db.Exports.Where(e => e.UserId == integrationEvent.UserId).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// Periodically removes exports whose upload was never completed, together with any object that was
/// uploaded for them.
/// </summary>
internal sealed class PendingExportCleanup(
    IServiceScopeFactory scopes,
    IObjectStorage storage,
    IOptions<StorageOptions> options,
    TimeProvider clock,
    ILogger<PendingExportCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
                var threshold = clock.GetUtcNow() - TimeSpan.FromHours(1);
                var stale = await db.Exports.Where(e => e.Status == ExportStatus.Pending && e.CreatedAt < threshold)
                    .Take(200).ToListAsync(stoppingToken);
                foreach (var export in stale)
                {
                    await storage.DeleteAsync(options.Value.Buckets.Exports, export.ObjectKey, stoppingToken);
                }

                db.Exports.RemoveRange(stale);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Pending export cleanup failed");
            }
        }
    }
}
