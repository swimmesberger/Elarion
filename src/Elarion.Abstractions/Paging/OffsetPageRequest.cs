namespace Elarion.Abstractions.Paging;

/// <summary>
/// A ready-made <see cref="IOffsetPageRequest"/> for handlers that need no extra filter fields.
/// Handlers that add filters should instead implement <see cref="IOffsetPageRequest"/> on their
/// own request record to keep the fields flat for binding.
/// </summary>
public sealed record OffsetPageRequest : IOffsetPageRequest {
    /// <inheritdoc />
    public int Page { get; init; } = 1;

    /// <inheritdoc />
    public int Size { get; init; } = 20;

    /// <inheritdoc />
    public string? Sort { get; init; }
}
