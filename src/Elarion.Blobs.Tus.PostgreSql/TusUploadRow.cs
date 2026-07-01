namespace Elarion.Blobs.Tus.PostgreSql;

/// <summary>
/// Durable staging row for an in-progress tus upload, persisted by
/// <see cref="PostgreSqlTusUploadStore{TDbContext}"/>.
/// </summary>
public sealed class TusUploadRow {
    /// <summary>The opaque upload id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The blob container the completed upload is stored in.</summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>The collision-safe storage name the completed upload is stored under.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The declared total size in bytes.</summary>
    public long UploadLength { get; set; }

    /// <summary>The number of bytes received so far.</summary>
    public long UploadOffset { get; set; }

    /// <summary>The content type the completed blob is stored with.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>The raw tus <c>Upload-Metadata</c> header value, or <c>null</c>.</summary>
    public string? Metadata { get; set; }

    /// <summary>The id of the user that created the upload, or <c>null</c>.</summary>
    public string? OwnerId { get; set; }

    /// <summary>When the incomplete session expires and may be reclaimed.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the session was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The id of the produced blob once complete, or <c>null</c> while in progress.</summary>
    public string? BlobId { get; set; }

    /// <summary>The staged bytes received so far; cleared once the upload completes.</summary>
    public byte[] Data { get; set; } = [];
}
