namespace Elarion.Blobs;

/// <summary>
/// Details for creating a staged upload session. The container and storage name are resolved by the
/// transport (from the owner and its protocol metadata) so the store stays free of naming policy;
/// expiry likewise arrives as an instant, so the store carries no expiry policy of its own.
/// </summary>
public sealed record StagedUploadCreation {
    /// <summary>The blob container the completed upload is stored in.</summary>
    public required string Container { get; init; }

    /// <summary>The collision-safe storage name the completed upload is stored under.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The declared total size in bytes, or <c>null</c> to defer the length: the upload grows until the
    /// transport calls <see cref="IStagedUploadStore.CompleteAsync"/>. Transports that defer must bound
    /// each appended chunk themselves.
    /// </summary>
    public required long? Length { get; init; }

    /// <summary>The content type the completed blob is stored with.</summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Opaque transport metadata to carry with the session (for example the raw tus
    /// <c>Upload-Metadata</c> header), or <c>null</c>. Stores round-trip it without interpreting it.
    /// </summary>
    public string? Metadata { get; init; }

    /// <summary>The id of the user creating the upload, or <c>null</c> when anonymous.</summary>
    public string? OwnerId { get; init; }

    /// <summary>
    /// When the incomplete session expires and may be reclaimed. Supplied by the transport (its upload
    /// expiry window applied to "now"), never computed by the store.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
