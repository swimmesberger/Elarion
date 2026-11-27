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

/// <summary>
/// The instance-side twin of <see cref="IResultFailureFactory{TSelf}"/>, so generic code with an unconstrained response
/// type can build a failure through <see cref="ResultInspection.TryCreateFailure{T}"/> without reflection. Internal: the
/// result types of this assembly implement it explicitly.
/// </summary>
internal interface IResultFailureSource<out TSelf> {
    TSelf CreateFailure(AppError error);
}
