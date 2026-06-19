namespace Elarion.Abstractions.Paging;

/// <summary>
/// A ready-made <see cref="IKeysetPageRequest"/> for handlers that need no extra filter fields.
/// Handlers that add filters (e.g. a search term) should instead implement
/// <see cref="IKeysetPageRequest"/> on their own request record to keep the fields flat for binding.
/// </summary>
public sealed record KeysetPageRequest : IKeysetPageRequest {
    /// <inheritdoc />
    public string? After { get; init; }

    /// <inheritdoc />
    public string? Before { get; init; }

    /// <inheritdoc />
    public int Size { get; init; } = 20;
}
