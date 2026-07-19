namespace Elarion.Blobs;

/// <summary>
/// Stages a resumable upload: bytes accumulate across any number of offset-guarded appends and, on
/// explicit completion, become a single <see cref="BlobLifecycleState.Pending"/> blob.
/// </summary>
/// <remarks>
/// <para>
/// This is the protocol-neutral staging seam. Resumable upload transports — tus 1.0 today, the IETF
/// resumable-uploads successor (draft-ietf-httpbis-resumable-upload) tomorrow — are adapters over it,
/// so a staging backend is written once and every transport lights up. All policy lives with the
/// caller: expiry instants arrive as data (<see cref="StagedUploadCreation.ExpiresAt"/>,
/// <see cref="StagedUploadCompletion"/>), never from store-side options.
/// </para>
/// <para>
/// Completion is explicit rather than inferred from the declared length: a transport may not know the
/// length up front (<see cref="StagedUploadCreation.Length"/> is <c>null</c>, the tus
/// <c>creation-defer-length</c> extension) and signals the end of the upload itself (the IETF draft's
/// <c>Upload-Complete</c> flag). <see cref="CompleteAsync"/> is idempotent, so a caller that crashed
/// between the last append and completion simply retries.
/// </para>
/// <para>
/// The produced <see cref="BlobRef"/> must resolve through the registered <see cref="IBlobStore"/> and
/// participate in the pending/committed lifecycle; how the bytes get there — a generic
/// <see cref="IBlobStore.SaveAsync"/> or a backend-native server-side copy — is the implementation's
/// choice, which is why a provider ships its blob store and staging store as a matched pair.
/// </para>
/// </remarks>
public interface IStagedUploadStore {
    /// <summary>Creates a new, empty upload session.</summary>
    Task<StagedUpload> CreateAsync(StagedUploadCreation creation, CancellationToken cancellationToken);

    /// <summary>Returns the upload session, or <c>null</c> when it does not exist.</summary>
    Task<StagedUpload?> GetAsync(string uploadId, CancellationToken cancellationToken);

    /// <summary>
    /// Appends a chunk at <paramref name="offset"/> and returns the updated session. When the session
    /// declares a <see cref="StagedUpload.Length"/> the store reads at most the remaining bytes; when
    /// the length is deferred the caller bounds the chunk.
    /// </summary>
    /// <exception cref="StagedUploadConflictException">
    /// Thrown when <paramref name="offset"/> does not match the session's current offset, the session
    /// is already complete, or it does not exist.
    /// </exception>
    Task<StagedUpload> AppendAsync(string uploadId, long offset, Stream chunk, CancellationToken cancellationToken);

    /// <summary>
    /// Seals the staged bytes into a <see cref="BlobLifecycleState.Pending"/> blob and returns the
    /// session with <see cref="StagedUpload.BlobRef"/> set. Idempotent: completing an already-completed
    /// session returns it unchanged, so a caller that crashed after the last append can retry.
    /// </summary>
    /// <exception cref="StagedUploadConflictException">
    /// Thrown when the session does not exist, or declares a length the received bytes do not reach
    /// (a premature completion).
    /// </exception>
    Task<StagedUpload> CompleteAsync(string uploadId, StagedUploadCompletion completion,
        CancellationToken cancellationToken);

    /// <summary>Deletes an upload session and any staged bytes.</summary>
    Task DeleteAsync(string uploadId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes sessions whose expiry is before <paramref name="olderThanUtc"/>, up to
    /// <paramref name="batchSize"/>. Returns the number deleted. (Garbage-collection entry point:
    /// reaps both incomplete sessions past their upload expiry and completed sessions past the
    /// retention stamped by <see cref="StagedUploadCompletion.SessionExpiresAt"/>.)
    /// </summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset olderThanUtc, int batchSize, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when a staged-upload operation conflicts with the session's current state: an append at a
/// stale offset or to a completed session, a premature completion, or an operation on a session that
/// does not exist. Transports surface it as their protocol's conflict (tus: <c>409</c>).
/// </summary>
public sealed class StagedUploadConflictException(string message) : Exception(message);
