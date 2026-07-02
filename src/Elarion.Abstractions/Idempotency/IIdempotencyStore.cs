namespace Elarion.Abstractions.Idempotency;

/// <summary>The fully-qualified key an idempotency record is stored under: an operation, a scope, an owner, and the key value.</summary>
/// <param name="Operation">The operation identity (the handler's request type name). It discriminates the key so
/// two different <c>[Idempotent]</c> handlers sharing a client-supplied key never collide on one record — the
/// second operation must not replay the first's stored response.</param>
/// <param name="Scope">The logical scope (<see cref="IdempotencyScope"/>).</param>
/// <param name="Owner">The owner discriminator within the scope — a hashed user id for
/// <see cref="IdempotencyScope.CurrentUser"/>, empty for <see cref="IdempotencyScope.Global"/>.</param>
/// <param name="Key">The client-supplied idempotency key value.</param>
public readonly record struct IdempotencyStoreKey(string Operation, IdempotencyScope Scope, string Owner, string Key);

/// <summary>
/// The atomic idempotency-record store. Implementations claim, complete, abandon, and purge keys; the durable
/// EF Core implementation (opt-in sibling package) writes the record in the caller's transaction and relies on
/// a database unique constraint to serialize concurrent duplicates across nodes.
/// </summary>
public interface IIdempotencyStore {
    /// <summary>
    /// Attempts to claim <paramref name="key"/> within the ambient unit-of-work. On a first-seen key it records
    /// a pending marker and returns <see cref="IdempotencyBeginStatus.Began"/>; on an already-completed key it
    /// returns <see cref="IdempotencyBeginStatus.Replay"/> with the stored payload; on a concurrent in-flight
    /// duplicate it returns <see cref="IdempotencyBeginStatus.InProgress"/> (or blocks then replays, per
    /// <paramref name="conflictBehavior"/>); on a fingerprint mismatch it returns
    /// <see cref="IdempotencyBeginStatus.FingerprintMismatch"/>.
    /// </summary>
    ValueTask<IdempotencyBeginResult> TryBeginAsync(
        IdempotencyStoreKey key,
        string fingerprint,
        IdempotencyConflictBehavior conflictBehavior,
        CancellationToken ct);

    /// <summary>
    /// Finalizes a claimed key with its serialized outcome and a retention window (the record becomes
    /// replayable until it expires). <paramref name="isFailure"/> flags a stored definitive-failure outcome.
    /// </summary>
    ValueTask CompleteAsync(
        IdempotencyStoreKey key,
        string payload,
        bool isFailure,
        TimeSpan retention,
        CancellationToken ct);

    /// <summary>
    /// Releases a claimed-but-not-completed key so it stays retryable. For a transactional backend this is a
    /// no-op (rolling back the unit of work discards the pending marker); the in-memory store removes its
    /// pending entry.
    /// </summary>
    ValueTask AbandonAsync(IdempotencyStoreKey key, CancellationToken ct);

    /// <summary>Deletes completed records whose retention window ended before <paramref name="olderThanUtc"/>.</summary>
    ValueTask<int> PurgeCompletedAsync(DateTimeOffset olderThanUtc, CancellationToken ct);
}
