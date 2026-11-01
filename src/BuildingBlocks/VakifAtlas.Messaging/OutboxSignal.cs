using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace VakifAtlas.Messaging;

/// <summary>Wakes the outbox dispatcher right after a save, since clients read <c>gamification/me</c> straight after contributing.</summary>
public sealed class OutboxSignal : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    /// <summary>Wakes the waiting dispatcher.</summary>
    public void Notify()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled.
        }
    }

    /// <summary>Waits until notified or the timeout elapses.</summary>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="cancellationToken">Token that cancels the wait.</param>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(timeout, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();
}

/// <summary>Signals the outbox after saves outside a transaction and after transaction commits.</summary>
/// <param name="signal">Signal that wakes the dispatcher.</param>
public sealed class OutboxSignalInterceptor(OutboxSignal signal) : SaveChangesInterceptor, IDbTransactionInterceptor
{
    /// <inheritdoc />
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context?.Database.CurrentTransaction is null)
        {
            signal.Notify();
        }

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        signal.Notify();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => signal.Notify();
}
