namespace Elarion.Abstractions;

/// <summary>
/// Marker interface for the Result pattern that exposes the failure <see cref="AppError"/>, letting a
/// behavior inspect <em>why</em> a result failed (its <see cref="AppError.Kind"/>) without knowing the
/// concrete value type. The <see cref="Error"/> value is only meaningful when
/// <see cref="IResultLike.IsSuccess"/> is <see langword="false"/>.
/// </summary>
public interface IResultError : IResultLike {
    /// <summary>The error. Only valid when <see cref="IResultLike.IsSuccess"/> is <see langword="false"/>.</summary>
    AppError Error { get; }
}
