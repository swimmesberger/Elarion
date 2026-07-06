namespace Elarion.Blobs.AspNetCore;

/// <summary>
/// The ownership rule shared by the direct blob-transfer endpoints (upload cancel and download): a blob is
/// accessible only to the exact recorded owner.
/// </summary>
internal static class BlobEndpointOwnership {
    /// <summary>
    /// Ownership is compared against the recorded owner id exactly, not parsed from the storage name, so an
    /// owner id that happens to contain the naming separator cannot be forged. A blob with no recorded owner
    /// is denied to everyone (fail closed).
    /// </summary>
    public static bool IsOwnedBy(BlobMetadata metadata, string ownerId) =>
        metadata.OwnerId is not null && string.Equals(metadata.OwnerId, ownerId, StringComparison.Ordinal);
}
