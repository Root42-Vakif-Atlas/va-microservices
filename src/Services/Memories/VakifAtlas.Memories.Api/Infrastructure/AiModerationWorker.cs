using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.CatalogClient;
using VakifAtlas.Contracts;
using VakifAtlas.Memories.Domain;
using VakifAtlas.Messaging;

namespace VakifAtlas.Memories.Api.Infrastructure;

/// <summary>
/// Paylaşım anında skorlanamayan (<c>unscored</c>) anıları yeniden dener (ADR 58). Skorlama satır kilidi
/// dışında yapılır; karar <c>FOR UPDATE</c> altında, anı hâlâ bekliyor ve skorlanmamışsa uygulanır (personel
/// arada karar verdiyse dokunulmaz). Pencere dışındaki anılar yalnızca VGM kuyruğunda kalır.
/// </summary>
internal sealed class AiModerationWorker(
    IServiceScopeFactory scopes,
    IOptions<ModerationOptions> options,
    TimeProvider clock,
    ILogger<AiModerationWorker> logger) : BackgroundService
{
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.Ai.RetryIntervalSeconds)), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "AI moderation retry failed");
            }
        }
    }

    /// <summary>Bir tur: en eski skorlanmamış anılar; skorlanan sayısını döner.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoriesDbContext>();
        var scoring = scope.ServiceProvider.GetRequiredService<AiScoring>();
        var since = clock.GetUtcNow() - TimeSpan.FromMinutes(options.Value.Ai.RetryWindowMinutes);
        var candidates = await db.Memories.AsNoTracking()
            .Where(m => m.Status == MemoryStatus.Pending && m.AiVerdict == AiVerdict.Unscored && m.CreatedAt >= since)
            .OrderBy(m => m.CreatedAt).Take(BatchSize).ToListAsync(cancellationToken);

        var scored = 0;
        foreach (var candidate in candidates)
        {
            if (await scoring.TryScoreAsync(candidate, cancellationToken) is not { } result)
            {
                // Servis hâlâ kapalı: sonraki tura kalır.
                break;
            }

            if (await ApplyAsync(scope.ServiceProvider, db, candidate.Id, result, cancellationToken))
            {
                scored++;
            }
        }

        return scored;
    }

    private async Task<bool> ApplyAsync(IServiceProvider services, MemoriesDbContext db, string id, AiScoringResult result,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var memory = await db.Memories.FromSql($"SELECT * FROM memories WHERE id = {id} FOR UPDATE SKIP LOCKED")
            .SingleOrDefaultAsync(cancellationToken);
        if (memory is not { Status: MemoryStatus.Pending, AiVerdict: AiVerdict.Unscored })
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var events = services.GetRequiredService<IEventPublisher>();
        if (memory.ApplyAi(result.Assessment, result.LatencyMs, now))
        {
            var catalog = services.GetRequiredService<ICatalogClient>();
            var monument = (await catalog.GetAsync([memory.MonumentId], "tr", cancellationToken)).GetValueOrDefault(memory.MonumentId);
            events.Publish(new MemoryPublished(Guid.CreateVersion7(now), now, memory.AuthorId, memory.Id,
                new ContributionPlace(memory.MonumentId, monument?.City ?? string.Empty)));
        }

        if (memory.DecidedByAi)
        {
            events.Publish(new MemoryModerated(Guid.CreateVersion7(now), now, memory.Id, memory.AuthorId,
                memory.Status == MemoryStatus.Approved ? ModerationDecision.Approve : ModerationDecision.Reject));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
