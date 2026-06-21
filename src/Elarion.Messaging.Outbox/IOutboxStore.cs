namespace Elarion.Messaging.Outbox;

/// <summary>
/// Storage seam for the outbox, scoped to the ambient unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Append"/> runs on the request scope's <see cref="Microsoft.EntityFrameworkCore.DbContext"/> so the
/// outbox row is saved and committed atomically with the business data — the publisher's unit of work owns
/// <c>SaveChanges</c>. The claim and finalize operations run on the delivery worker's own scope.
/// </para>
/// <para>
/// Isolating EF Core behind this seam keeps the bus, dispatcher, and delivery loop database-agnostic and unit-testable.
/// </para>
/// </remarks>
public interface IOutboxStore
{
    /// <summary>
    /// Tracks <paramref name="message"/> for insertion as part of the caller's unit of work. Does not call
    /// <c>SaveChanges</c>; the row is persisted when the unit of work commits, and discarded if it rolls back.
    /// </summary>
    void Append(OutboxMessage message);

    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> pending messages for the lease holder
    /// <paramref name="lockId"/> until <paramref name="leaseUntil"/>, and returns the claimed messages.
    /// </summary>
    ValueTask<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        Guid lockId,
        DateTimeOffset leaseUntil,
        int batchSize,
        CancellationToken ct);

    /// <summary>Marks the message delivered and clears its lease.</summary>
    ValueTask MarkProcessedAsync(Guid id, DateTimeOffset processedOnUtc, CancellationToken ct);

    /// <summary>Records a failed delivery attempt, stores <paramref name="error"/>, and releases the lease for retry.</summary>
    ValueTask MarkFailedAsync(Guid id, string error, CancellationToken ct);

    /// <summary>Permanently deletes delivered messages whose <see cref="OutboxMessage.ProcessedOnUtc"/> is before <paramref name="olderThanUtc"/>.</summary>
    ValueTask<int> PurgeProcessedAsync(DateTimeOffset olderThanUtc, CancellationToken ct);
}
