namespace Elarion.Blobs;

/// <summary>
/// Optional capability over <see cref="IBlobStore"/> that promotes pending uploads to committed and
/// reclaims abandoned ones.
/// </summary>
/// <remarks>
/// This is a separate interface so a backend that cannot model a two-state lifecycle is not forced to
/// implement it. A backend that can (for example the PostgreSQL store) implements both
/// <see cref="IBlobStore"/> and this interface on the same type, sharing one unit of work so a commit
/// participates in the caller's transaction.
/// </remarks>
public interface IBlobLifecycle {
    /// <summary>
    /// Promotes a <see cref="BlobLifecycleState.Pending"/> blob to
    /// <see cref="BlobLifecycleState.Committed"/>, clearing its expiry so it is never garbage collected.
    /// </summary>
    /// <param name="blobRef">Reference to the blob to commit.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// <c>true</c> when the blob exists and is now (or already was) committed; <c>false</c> when it does
    /// not exist.
    /// </returns>
    /// <remarks>
    /// The change participates in the caller's unit of work: implementations mutate within the ambient
    /// transaction (or defer to the caller's next save) so the commit and the entity insert that
    /// references the blob are atomic. Call this inside the same transaction that persists the
    /// referencing entity; if that transaction rolls back, the blob stays pending and is reclaimed by
    /// garbage collection. The operation is idempotent — committing an already-committed blob succeeds.
    /// </remarks>
    Task<bool> CommitAsync(BlobRef blobRef, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes <see cref="BlobLifecycleState.Pending"/> blobs whose expiry is before
    /// <paramref name="olderThanUtc"/>, up to <paramref name="batchSize"/> rows.
    /// </summary>
    /// <param name="olderThanUtc">Blobs expiring before this instant are reclaimed.</param>
    /// <param name="batchSize">The maximum number of blobs to delete in one call.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The number of blobs deleted.</returns>
    /// <remarks>
    /// This is the garbage-collection entry point. It is committed independently of any caller
    /// transaction and deletes only rows still pending, so it never reclaims a blob committed
    /// concurrently. Blob content is removed by the backend's cascade.
    /// </remarks>
    Task<int> DeleteExpiredPendingAsync(DateTimeOffset olderThanUtc, int batchSize,
        CancellationToken cancellationToken);
}
