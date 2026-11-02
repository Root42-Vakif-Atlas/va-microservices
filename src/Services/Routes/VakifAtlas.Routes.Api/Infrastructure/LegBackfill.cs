using Microsoft.EntityFrameworkCore;
using VakifAtlas.CatalogClient;
using VakifAtlas.ServiceDefaults.Http;

namespace VakifAtlas.Routes.Api.Infrastructure;

/// <summary>
/// Başka motorla hesaplanmış rotaların (eski düz çizgi tahmini, önceki OSRM verisi) bacaklarını yeniden
/// hesaplar; liste özetindeki mesafe ve süre de güncellenir. Detay isteği aynı rotayı ayrıca anında yeniler.
/// Konumsuz veya katalogdan kalkmış durağı olan rota atlanır.
/// </summary>
internal sealed class LegBackfill(IServiceScopeFactory scopes, TimeProvider clock, ILogger<LegBackfill> logger) : BackgroundService
{
    private const int BatchSize = 25;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await RunAsync(stoppingToken);
                delay = Interval;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Açılışta katalog veya OSRM henüz hazır olmayabilir.
                logger.LogWarning(exception, "Route leg backfill failed");
                delay = RetryDelay;
            }

            await Task.Delay(delay, clock, stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        string? after = null;
        string? engine = null;
        var refreshed = 0;
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoutesDbContext>();
            var legs = scope.ServiceProvider.GetRequiredService<LegCalculator>();
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogClient>();
            engine = legs.Engine;
            var batch = await db.Routes
                .Where(r => r.LegsEngine != engine && (after == null || string.Compare(r.Id, after) > 0))
                .OrderBy(r => r.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            after = batch[^1].Id;
            var found = await catalog.GetAsync(batch.SelectMany(r => r.Stops.Select(s => s.MonumentId)), RequestLanguage.Turkish,
                cancellationToken);
            foreach (var route in batch)
            {
                var stops = route.Stops.Select(s => found.GetValueOrDefault(s.MonumentId)).ToArray();
                if (stops.Any(m => m?.Point is null))
                {
                    continue;
                }

                route.RefreshLegs(await legs.CalculateAsync(stops!, cancellationToken), engine);
                refreshed++;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        if (refreshed > 0)
        {
            logger.LogInformation("Recalculated legs of {Count} routes with {Engine}", refreshed, engine);
        }
    }
}
