namespace Elarion.Abstractions;

/// <summary>
/// Represents a result type that can be created from an <see cref="AppError"/>.
/// </summary>
/// <typeparam name="TSelf">The concrete result type.</typeparam>
public interface IResultFailureFactory<TSelf>
    where TSelf : IResultFailureFactory<TSelf> {
    /// <summary>
    /// Creates a failed result wrapping <paramref name="error"/>.
    /// </summary>
    static abstract TSelf Failure(AppError error);
}

