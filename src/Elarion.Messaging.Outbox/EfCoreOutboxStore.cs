using Microsoft.EntityFrameworkCore;

namespace Elarion.Messaging.Outbox;

/// <summary>EF Core transactional storage for immutable messages and per-consumer deliveries.</summary>
public sealed class EfCoreOutboxStore<TDbContext>(TDbContext dbContext, OutboxOptions options, TimeProvider timeProvider)
    : IOutboxStore
    where TDbContext : DbContext {
    private const int PurgeBatchSize = 1_000;

    /// <inheritdoc />
    public void Append(OutboxMessage message) {
        ArgumentNullException.ThrowIfNull(message);
        dbContext.Set<OutboxMessage>().Add(message);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxDelivery>> ClaimPendingAsync(
        Guid lockId,
        DateTimeOffset leaseUntil,
        int batchSize,
        IReadOnlyCollection<string> heldRoles,
        CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(heldRoles);
        var now = timeProvider.GetUtcNow();
        var maxAttempts = options.MaxDeliveryAttempts;
        var roles = heldRoles.Count == 0 ? [] : heldRoles.ToArray();

        var candidateIds = await dbContext.Set<OutboxDelivery>()
            .AsNoTracking()
            .Where(delivery => delivery.ProcessedOnUtc == null
                && delivery.Attempts < maxAttempts
                && (delivery.LockedUntilUtc == null || delivery.LockedUntilUtc < now)
                && (delivery.TargetRole == null || roles.Contains(delivery.TargetRole)))
            .OrderBy(delivery => delivery.OccurredOnUtc)
            .ThenBy(delivery => delivery.Id)
            .Take(batchSize)
            .Select(delivery => delivery.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidateIds.Count == 0) {
            return [];
        }

        await dbContext.Set<OutboxDelivery>()
            .Where(delivery => candidateIds.Contains(delivery.Id)
                && delivery.ProcessedOnUtc == null
                && delivery.Attempts < maxAttempts
                && (delivery.LockedUntilUtc == null || delivery.LockedUntilUtc < now)
                && (delivery.TargetRole == null || roles.Contains(delivery.TargetRole)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(delivery => delivery.LockId, lockId)
                    .SetProperty(delivery => delivery.LockedUntilUtc, leaseUntil),
                ct)
            .ConfigureAwait(false);

        return await dbContext.Set<OutboxDelivery>()
            .AsNoTracking()
            .Include(delivery => delivery.Message)
            .Where(delivery => candidateIds.Contains(delivery.Id) && delivery.LockId == lockId)
            .OrderBy(delivery => delivery.OccurredOnUtc)
            .ThenBy(delivery => delivery.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> ReleaseClaimAsync(Guid deliveryId, Guid lockId, CancellationToken ct) {
        var rows = await dbContext.Set<OutboxDelivery>()
            .Where(delivery => delivery.Id == deliveryId && delivery.LockId == lockId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(delivery => delivery.LockId, (Guid?)null)
                    .SetProperty(delivery => delivery.LockedUntilUtc, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkProcessedAsync(
        Guid deliveryId,
        Guid lockId,
        DateTimeOffset processedOnUtc,
        CancellationToken ct) {
        var rows = await dbContext.Set<OutboxDelivery>()
            .Where(delivery => delivery.Id == deliveryId && delivery.LockId == lockId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(delivery => delivery.ProcessedOnUtc, processedOnUtc)
                    .SetProperty(delivery => delivery.LockId, (Guid?)null)
                    .SetProperty(delivery => delivery.LockedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(delivery => delivery.Error, (string?)null),
                ct)
            .ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkFailedAsync(
        Guid deliveryId,
        Guid lockId,
        string error,
        DateTimeOffset retryVisibleAfterUtc,
        CancellationToken ct) {
        var rows = await dbContext.Set<OutboxDelivery>()
            .Where(delivery => delivery.Id == deliveryId && delivery.LockId == lockId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(delivery => delivery.Attempts, delivery => delivery.Attempts + 1)
                    .SetProperty(delivery => delivery.Error, error)
                    .SetProperty(delivery => delivery.LockId, (Guid?)null)
                    .SetProperty(delivery => delivery.LockedUntilUtc, retryVisibleAfterUtc),
                ct)
            .ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkPermanentlyFailedAsync(
        Guid deliveryId,
        Guid lockId,
        string error,
        CancellationToken ct) {
        var maxAttempts = options.MaxDeliveryAttempts;
        var rows = await dbContext.Set<OutboxDelivery>()
            .Where(delivery => delivery.Id == deliveryId && delivery.LockId == lockId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(delivery => delivery.Attempts, maxAttempts)
                    .SetProperty(delivery => delivery.Error, error)
                    .SetProperty(delivery => delivery.LockId, (Guid?)null)
                    .SetProperty(delivery => delivery.LockedUntilUtc, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<int> PurgeProcessedAsync(DateTimeOffset olderThanUtc, CancellationToken ct) {
        var purged = 0;
        while (true) {
            // Start from the processed-delivery index. NOT EXISTS rejects a message when any sibling
            // delivery is pending or too recent; Take bounds both the candidate query and parent DELETE.
            var candidates = await dbContext.Set<OutboxDelivery>()
                .AsNoTracking()
                .Where(delivery => delivery.ProcessedOnUtc != null
                    && delivery.ProcessedOnUtc < olderThanUtc
                    && !dbContext.Set<OutboxDelivery>().Any(sibling =>
                        sibling.MessageId == delivery.MessageId
                        && (sibling.ProcessedOnUtc == null || sibling.ProcessedOnUtc >= olderThanUtc)))
                .OrderBy(delivery => delivery.ProcessedOnUtc)
                .ThenBy(delivery => delivery.Id)
                .Select(delivery => delivery.MessageId)
                .Take(PurgeBatchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (candidates.Count == 0) {
                return purged;
            }

            var messageIds = candidates.Distinct().ToArray();
            purged += await dbContext.Set<OutboxMessage>()
                .Where(message => messageIds.Contains(message.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            if (candidates.Count < PurgeBatchSize) {
                return purged;
            }
        }
    }
}
