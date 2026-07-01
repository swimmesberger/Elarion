namespace Elarion.Blobs.Tus;

/// <summary>
/// The observable state of a tus upload session.
/// </summary>
public sealed record TusUpload {
    /// <summary>The opaque upload id (the last segment of the tus resource URL).</summary>
    public required string Id { get; init; }

    /// <summary>The declared total size in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>The number of bytes received so far.</summary>
    public required long Offset { get; init; }

    /// <summary>The content type the completed blob is stored with.</summary>
    public required string ContentType { get; init; }

    /// <summary>The raw tus <c>Upload-Metadata</c> header value, echoed back on <c>HEAD</c>, or <c>null</c>.</summary>
    public string? Metadata { get; init; }

    /// <summary>The id of the user that created the upload, or <c>null</c> when anonymous.</summary>
    public string? OwnerId { get; init; }

    /// <summary>When the incomplete session expires and may be reclaimed.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The reference to the produced (pending) blob once the upload has completed, or <c>null</c> while it
    /// is still in progress. This is the handle a client passes when creating the owning entity.
    /// </summary>
    public BlobRef? BlobRef { get; init; }

    /// <summary>Whether the upload has received all its bytes and produced a blob.</summary>
    public bool IsComplete => BlobRef is not null;
}
