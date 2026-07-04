namespace Elarion.Blobs;

/// <summary>
/// The observable state of a staged (resumable) upload session.
/// </summary>
public sealed record StagedUpload {
    /// <summary>The opaque upload id.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The declared total size in bytes, or <c>null</c> while the length is deferred (unknown until the
    /// transport completes the upload).
    /// </summary>
    public required long? Length { get; init; }

    /// <summary>The number of bytes received so far.</summary>
    public required long Offset { get; init; }

    /// <summary>The content type the completed blob is stored with.</summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Opaque transport metadata carried with the session (for example the raw tus
    /// <c>Upload-Metadata</c> header), or <c>null</c>. Stores round-trip it without interpreting it.
    /// </summary>
    public string? Metadata { get; init; }

    /// <summary>The id of the user that created the upload, or <c>null</c> when anonymous.</summary>
    public string? OwnerId { get; init; }

    /// <summary>
    /// When the session expires and may be reclaimed — the creation expiry while in progress, or the
    /// completed-retention deadline once complete.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The reference to the produced (pending) blob once the upload has completed, or <c>null</c> while
    /// it is still in progress. This is the handle a client passes when creating the owning entity.
    /// </summary>
    public BlobRef? BlobRef { get; init; }

    /// <summary>Whether the upload has been completed and produced a blob.</summary>
    public bool IsComplete => BlobRef is not null;
}
