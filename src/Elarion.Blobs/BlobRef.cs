namespace Elarion.Blobs;

/// <summary>
/// Lightweight reference to a stored blob.
/// </summary>
public readonly record struct BlobRef {
    /// <summary>
    /// Gets the unique identifier of the stored blob.
    /// </summary>
    public required string Value { get; init; }

    /// <inheritdoc />
    public override string ToString() {
        return Value;
    }
}
