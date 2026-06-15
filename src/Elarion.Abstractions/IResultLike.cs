namespace Elarion.Abstractions;

/// <summary>
/// Marker interface for the Result pattern, allowing behaviors to inspect
/// success/failure without knowing the concrete <typeparamref name="T"/>.
/// </summary>
public interface IResultLike {
    /// <summary>
    /// Indicates whether the result represents a successful operation.
    /// </summary>
    bool IsSuccess { get; }
}
