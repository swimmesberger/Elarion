using Microsoft.EntityFrameworkCore;

namespace Elarion.Messaging.Outbox;

/// <summary>
/// EF Core implementation of <see cref="IOutboxStore"/> backed by a <typeparamref name="TDbContext"/>.
/// </summary>
/// <typeparam name="TDbContext">The context whose model includes <see cref="OutboxMessage"/> via <c>UseElarionOutbox</c>.</typeparam>
/// <remarks>
/// Claiming reads an ordered, size-limited set of candidate ids, then stamps a fresh per-poll lease id onto those that
/// are still free with one conditional server-side <c>ExecuteUpdate</c>, and reads back the rows now bearing that lease
/// id — the rows this worker actually won. Two competing workers never deliver the same message because each conditional
/// update only claims still-free rows. This relies on no provider-specific locking and works on any relational provider.
/// </remarks>
public sealed class EfCoreOutboxStore<TDbContext>(TDbContext dbContext, OutboxOptions options, TimeProvider timeProvider)
    : IOutboxStore
    where TDbContext : DbContext
{
    /// <inheritdoc />
    public void Append(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        dbContext.Set<OutboxMessage>().Add(message);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        Guid lockId,
        DateTimeOffset leaseUntil,
        int batchSize,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var maxAttempts = options.MaxDeliveryAttempts;

        var candidateIds = await dbContext.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(message => message.ProcessedOnUtc == null
                && message.Attempts < maxAttempts
                && (message.LockedUntilUtc == null || message.LockedUntilUtc < now))
            .OrderBy(message => message.OccurredOnUtc)
            .Take(batchSize)
            .Select(message => message.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidateIds.Count == 0)
        {
            return [];
        }

        // Stamp this poll's lease id onto the candidates still free; concurrent workers see the guard and skip them.
        // The Attempts re-check matters: a message parked between the candidate read and this update
        // (MarkPermanentlyFailedAsync leaves LockId/LockedUntilUtc null with Attempts at the max) would otherwise
        // pass the lock guard and be delivered once more from a stale candidate list.
        await dbContext.Set<OutboxMessage>()
            .Where(message => candidateIds.Contains(message.Id)
                && message.ProcessedOnUtc == null
                && message.Attempts < maxAttempts
                && (message.LockedUntilUtc == null || message.LockedUntilUtc < now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.LockId, lockId)
                    .SetProperty(message => message.LockedUntilUtc, leaseUntil),
                ct)
            .ConfigureAwait(false);

        // The candidate rows now bearing this poll's unique lease id are exactly the ones this worker won.
        // Constraining to the candidate ids keeps the read-back a primary-key probe (LockId has no index).
        return await dbContext.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(message => candidateIds.Contains(message.Id) && message.LockId == lockId)
            .OrderBy(message => message.OccurredOnUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkProcessedAsync(Guid id, Guid lockId, DateTimeOffset processedOnUtc, CancellationToken ct)
    {
        var rows = await dbContext.Set<OutboxMessage>()
            .Where(message => message.Id == id && message.LockId == lockId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.ProcessedOnUtc, processedOnUtc)
                    .SetProperty(message => message.LockId, (Guid?)null)
                    .SetProperty(message => message.LockedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.Error, (string?)null),
                ct)
            .ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkFailedAsync(
        Guid id,
        Guid lockId,
        string error,
        DateTimeOffset retryVisibleAfterUtc,
        CancellationToken ct)
    {
        // Clear the lease owner but keep LockedUntilUtc in the future as a visibility timeout, so the claim guard
        // (LockedUntilUtc == null || LockedUntilUtc < now) skips this row until the backoff elapses — a poison
        // message no longer re-enters the front of every batch.
        var rows = await dbContext.Set<OutboxMessage>()
            .Where(message => message.Id == id && message.LockId == lockId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.Attempts, message => message.Attempts + 1)
                    .SetProperty(message => message.Error, error)
                    .SetProperty(message => message.LockId, (Guid?)null)
                    .SetProperty(message => message.LockedUntilUtc, retryVisibleAfterUtc),
                ct)
            .ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkPermanentlyFailedAsync(Guid id, Guid lockId, string error, CancellationToken ct)
    {
        // Set Attempts to the max so the claim guard's Attempts < maxAttempts filter permanently excludes the row —
        // the same "left for inspection, no longer retried" mechanism a run of ordinary failures reaches, applied at
        // once for a failure that can never succeed on retry.
        var maxAttempts = options.MaxDeliveryAttempts;
        var rows = await dbContext.Set<OutboxMessage>()
            .Where(message => message.Id == id && message.LockId == lockId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.Attempts, maxAttempts)
                    .SetProperty(message => message.Error, error)
                    .SetProperty(message => message.LockId, (Guid?)null)
                    .SetProperty(message => message.LockedUntilUtc, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<int> PurgeProcessedAsync(DateTimeOffset olderThanUtc, CancellationToken ct) =>
        await dbContext.Set<OutboxMessage>()
            .Where(message => message.ProcessedOnUtc != null && message.ProcessedOnUtc < olderThanUtc)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
}
