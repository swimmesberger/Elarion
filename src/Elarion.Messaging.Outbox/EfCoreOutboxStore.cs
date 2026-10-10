using Elarion.EntityFrameworkCore.LeasedWork;
using Microsoft.EntityFrameworkCore;

namespace Elarion.Messaging.Outbox;

/// <summary>EF Core transactional storage for role-grouped outbox envelopes.</summary>
/// <remarks>
/// Claims and finalizes ride the leased-work-row primitive (<see cref="LeasedWorkDbSetExtensions"/>, ADR-0073): the
/// outbox contributes its eligibility (unprocessed, under <see cref="OutboxOptions.MaxDeliveryAttempts"/>, target role
/// held), its queue order (<see cref="OutboxMessage.OccurredOnUtc"/>), and its finalize bookkeeping.
/// </remarks>
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
        var maxAttempts = options.MaxDeliveryAttempts;
        var roles = heldRoles.Count == 0 ? [] : heldRoles.ToArray();

        return await dbContext.Set<OutboxMessage>()
            .ClaimPendingAsync(
                new LeasedWorkClaim<OutboxMessage> {
                    LockId = lockId,
                    NowUtc = timeProvider.GetUtcNow(),
                    LeaseUntilUtc = leaseUntil,
                    BatchSize = batchSize,
                    Eligible = message => message.ProcessedOnUtc == null
                                          && message.Attempts < maxAttempts
                                          && (message.TargetRole == null || roles.Contains(message.TargetRole)),
                    OrderBy = messages => messages.OrderBy(message => message.OccurredOnUtc)
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> ReleaseClaimAsync(Guid groupId, Guid lockId, CancellationToken ct) =>
        await dbContext.Set<OutboxMessage>().ReleaseClaimAsync(groupId, lockId, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<bool> MarkProcessedAsync(
        Guid groupId,
        Guid lockId,
        DateTimeOffset processedOnUtc,
        CancellationToken ct) =>
        await dbContext.Set<OutboxMessage>()
            .FinalizeClaimAsync(
                groupId,
                lockId,
                setters => setters
                    .SetProperty(message => message.ProcessedOnUtc, processedOnUtc)
                    .SetProperty(message => message.Error, (string?)null),
                visibleAfterUtc: null,
                ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<bool> MarkFailedAsync(
        Guid groupId,
        Guid lockId,
        string error,
        DateTimeOffset retryVisibleAfterUtc,
        CancellationToken ct) =>
        await dbContext.Set<OutboxMessage>()
            .FinalizeClaimAsync(
                groupId,
                lockId,
                setters => setters
                    .SetProperty(message => message.Attempts, message => message.Attempts + 1)
                    .SetProperty(message => message.Error, error),
                retryVisibleAfterUtc,
                ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<bool> MarkPermanentlyFailedAsync(
        Guid groupId,
        Guid lockId,
        string error,
        CancellationToken ct) {
        var maxAttempts = options.MaxDeliveryAttempts;
        return await dbContext.Set<OutboxMessage>()
            .FinalizeClaimAsync(
                groupId,
                lockId,
                setters => setters
                    .SetProperty(message => message.Attempts, maxAttempts)
                    .SetProperty(message => message.Error, error),
                visibleAfterUtc: null,
                ct)
            .ConfigureAwait(false);
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
