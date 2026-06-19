namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Metadata row for a blob stored by <see cref="PostgreSqlBlobStore{TDbContext}"/>.
/// </summary>
public sealed class StoredBlob {
    /// <summary>
    /// Gets or sets the unique blob identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the logical grouping that contains the blob.
    /// </summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable blob name within the container.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the blob MIME type.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the content length in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when the blob was stored.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
