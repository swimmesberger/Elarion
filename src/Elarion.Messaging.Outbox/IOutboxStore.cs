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
    /// Atomically claims up to <paramref name="batchSize"/> eligible pending consumer deliveries.
    /// A role-bound delivery is eligible only when its target is present in <paramref name="heldRoles"/>.
    /// </summary>
    ValueTask<IReadOnlyList<OutboxDelivery>> ClaimPendingAsync(
        Guid lockId,
        DateTimeOffset leaseUntil,
        int batchSize,
        IReadOnlyCollection<string> heldRoles,
        CancellationToken ct);

    /// <summary>
    /// Marks the consumer delivery complete and clears its lease while <paramref name="lockId"/> still owns it.
    /// </summary>
    /// <remarks>
    /// The update is guarded on the caller's lease token so a worker whose lease expired and was reclaimed by another
    /// worker cannot overwrite the new owner's active lease. Returns <see langword="false"/> when no row matched
    /// (the lease was lost), so the caller can log and skip rather than assuming success.
    /// </remarks>
    /// <returns><see langword="true"/> when the row was updated; <see langword="false"/> when the lease was lost.</returns>
    ValueTask<bool> MarkProcessedAsync(Guid deliveryId, Guid lockId, DateTimeOffset processedOnUtc, CancellationToken ct);

    /// <summary>
    /// Records a failed attempt, stores <paramref name="error"/>, and releases the lease so the delivery
    /// becomes claimable again only after <paramref name="retryVisibleAfterUtc"/> (the backoff visibility timeout),
    /// but only while <paramref name="lockId"/> still owns the lease.
    /// </summary>
    /// <returns><see langword="true"/> when the row was updated; <see langword="false"/> when the lease was lost.</returns>
    ValueTask<bool> MarkFailedAsync(
        Guid deliveryId,
        Guid lockId,
        string error,
        DateTimeOffset retryVisibleAfterUtc,
        CancellationToken ct);

    /// <summary>
    /// Parks the delivery as permanently failed — it is never claimed again but kept for inspection — storing
    /// <paramref name="error"/>, but only while <paramref name="lockId"/> still owns the lease.
    /// </summary>
    /// <remarks>
    /// Used for a failure that can never succeed on retry (an unresolvable event type or a payload that deserializes
    /// to <see langword="null"/>), so retrying would only spin. The delivery stays pending-but-unclaimable.
    /// </remarks>
    /// <returns><see langword="true"/> when the row was updated; <see langword="false"/> when the lease was lost.</returns>
    ValueTask<bool> MarkPermanentlyFailedAsync(Guid deliveryId, Guid lockId, string error, CancellationToken ct);

    /// <summary>Permanently deletes messages whose every delivery completed before <paramref name="olderThanUtc"/>.</summary>
    ValueTask<int> PurgeProcessedAsync(DateTimeOffset olderThanUtc, CancellationToken ct);
}
