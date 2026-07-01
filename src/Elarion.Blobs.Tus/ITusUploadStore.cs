namespace Elarion.Blobs.Tus;

/// <summary>
/// Details for creating a tus upload session. The container and storage name are resolved by the endpoint
/// (from the owner and tus metadata) so the store stays free of naming policy.
/// </summary>
public sealed record TusUploadCreation {
    /// <summary>The blob container the completed upload is stored in.</summary>
    public required string Container { get; init; }

    /// <summary>The collision-safe storage name the completed upload is stored under.</summary>
    public required string Name { get; init; }

    /// <summary>The declared total size in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>The content type the completed blob is stored with.</summary>
    public required string ContentType { get; init; }

    /// <summary>The raw tus <c>Upload-Metadata</c> header value, or <c>null</c>.</summary>
    public string? Metadata { get; init; }

    /// <summary>The id of the user creating the upload, or <c>null</c> when anonymous.</summary>
    public string? OwnerId { get; init; }
}

/// <summary>
/// Stages a resumable (tus) upload: it accumulates the bytes across <c>PATCH</c> requests and, on
/// completion, writes them as a single pending blob via <see cref="IBlobStore"/>.
/// </summary>
/// <remarks>
/// The protocol handler resolves a store from DI; the in-memory store is the single-instance default and a
/// durable provider-backed store (for example PostgreSQL staging tables) replaces it for resumability
/// across restarts and instances.
/// </remarks>
public interface ITusUploadStore {
    /// <summary>Creates a new, empty upload session.</summary>
    Task<TusUpload> CreateAsync(TusUploadCreation creation, CancellationToken cancellationToken);

    /// <summary>Returns the upload session, or <c>null</c> when it does not exist.</summary>
    Task<TusUpload?> GetAsync(string uploadId, CancellationToken cancellationToken);

    /// <summary>
    /// Appends a chunk at <paramref name="offset"/> and returns the updated session. When the session
    /// reaches its declared length, the store finalizes it into a pending blob and the returned session's
    /// <see cref="TusUpload.BlobRef"/> is set.
    /// </summary>
    /// <exception cref="TusOffsetConflictException">
    /// Thrown when <paramref name="offset"/> does not match the session's current offset, or the session
    /// does not exist.
    /// </exception>
    Task<TusUpload> AppendAsync(string uploadId, long offset, Stream chunk, CancellationToken cancellationToken);

    /// <summary>Deletes an upload session and any staged bytes.</summary>
    Task DeleteAsync(string uploadId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes incomplete sessions whose expiry is before <paramref name="olderThanUtc"/>, up to
    /// <paramref name="batchSize"/>. Returns the number deleted. (Garbage-collection entry point.)
    /// </summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset olderThanUtc, int batchSize, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when a tus <c>PATCH</c> offset does not match the session's current offset.
/// </summary>
public sealed class TusOffsetConflictException() : Exception("The upload offset did not match the current offset.");
