using Microsoft.EntityFrameworkCore;

namespace Elarion.Messaging.Outbox;

/// <summary>EF Core transactional storage for role-grouped outbox envelopes.</summary>
public sealed class EfCoreOutboxStore<TDbContext>(
    TDbContext dbContext,
    OutboxOptions options,
    TimeProvider timeProvider)
    : IOutboxStore
    where TDbContext : DbContext {
    private const int PurgeBatchSize = 1_000;

    /// <inheritdoc />
    public void Append(OutboxMessage message) {
        ArgumentNullException.ThrowIfNull(message);
        dbContext.Set<OutboxMessage>().Add(message);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        Guid lockId,
        DateTimeOffset leaseUntil,
        int batchSize,
        IReadOnlyCollection<string> heldRoles,
        CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(heldRoles);
        var now = timeProvider.GetUtcNow();
        var maxAttempts = options.MaxDeliveryAttempts;
        var roles = heldRoles.Count == 0 ? [] : heldRoles.ToArray();

        var candidateIds = await dbContext.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(message => message.ProcessedOnUtc == null
                              && message.Attempts < maxAttempts
                              && (message.LockedUntilUtc == null || message.LockedUntilUtc < now)
                              && (message.TargetRole == null || roles.Contains(message.TargetRole)))
            .OrderBy(message => message.OccurredOnUtc)
            .ThenBy(message => message.Id)
            .Take(batchSize)
            .Select(message => message.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidateIds.Count == 0) return [];

        await dbContext.Set<OutboxMessage>()
            .Where(message => candidateIds.Contains(message.Id)
                              && message.ProcessedOnUtc == null
                              && message.Attempts < maxAttempts
                              && (message.LockedUntilUtc == null || message.LockedUntilUtc < now)
                              && (message.TargetRole == null || roles.Contains(message.TargetRole)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.LockId, lockId)
                    .SetProperty(message => message.LockedUntilUtc, leaseUntil),
                ct)
            .ConfigureAwait(false);

        return await dbContext.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(message => candidateIds.Contains(message.Id) && message.LockId == lockId)
            .OrderBy(message => message.OccurredOnUtc)
            .ThenBy(message => message.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> ReleaseClaimAsync(Guid groupId, Guid lockId, CancellationToken ct) {
        var rows = await dbContext.Set<OutboxMessage>()
            .Where(message => message.Id == groupId && message.LockId == lockId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.LockId, (Guid?)null)
                    .SetProperty(message => message.LockedUntilUtc, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkProcessedAsync(
        Guid groupId,
        Guid lockId,
        DateTimeOffset processedOnUtc,
        CancellationToken ct) {
        var rows = await dbContext.Set<OutboxMessage>()
            .Where(message => message.Id == groupId && message.LockId == lockId)
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
        Guid groupId,
        Guid lockId,
        string error,
        DateTimeOffset retryVisibleAfterUtc,
        CancellationToken ct) {
        var rows = await dbContext.Set<OutboxMessage>()
            .Where(message => message.Id == groupId && message.LockId == lockId)
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
    public async ValueTask<bool> MarkPermanentlyFailedAsync(
        Guid groupId,
        Guid lockId,
        string error,
        CancellationToken ct) {
        var maxAttempts = options.MaxDeliveryAttempts;
        var rows = await dbContext.Set<OutboxMessage>()
            .Where(message => message.Id == groupId && message.LockId == lockId)
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
    public async ValueTask<int> PurgeProcessedAsync(DateTimeOffset olderThanUtc, CancellationToken ct) {
        var purged = 0;
        while (true) {
            var candidates = await dbContext.Set<OutboxMessage>()
                .AsNoTracking()
                .Where(message => message.ProcessedOnUtc != null
                                  && message.ProcessedOnUtc < olderThanUtc)
                .OrderBy(message => message.ProcessedOnUtc)
                .ThenBy(message => message.Id)
                .Select(message => message.Id)
                .Take(PurgeBatchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (candidates.Count == 0) return purged;

            purged += await dbContext.Set<OutboxMessage>()
                .Where(message => candidates.Contains(message.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            if (candidates.Count < PurgeBatchSize) return purged;
        }
    }
}
