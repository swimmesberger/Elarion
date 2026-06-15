namespace Elarion.Abstractions;

/// <summary>
/// A value-type result that wraps either a success value <typeparamref name="T"/>
/// or an <see cref="AppError"/>. Used as the return type for all application handlers.
/// Implicit conversions allow ergonomic handler code.
/// </summary>
/// <example>
/// <code>
/// // Returning success via implicit conversion
/// return new Response(project.Id, project.Name);
///
/// // Returning failure via implicit conversion
/// return AppError.NotFound($"Project {query.Id} not found");
/// </code>
/// </example>
public readonly struct Result<T> : IResultLike, IResultFailureFactory<Result<T>> {
    /// <inheritdoc />
    public bool IsSuccess { get; }

    /// <summary>The success value. Only valid when <see cref="IsSuccess"/> is <c>true</c>.</summary>
    public T Value { get; }

    /// <summary>The error. Only valid when <see cref="IsSuccess"/> is <c>false</c>.</summary>
    public AppError Error { get; }

    // Note 1: The two private constructors make the success/failure state impossible to mix accidentally.
    private Result(T value) {
        IsSuccess = true;
        // Note 2: default! is safe here because Error is intentionally unreadable for successful results.
        Value = value;
        Error = default!;
    }

    private Result(AppError error) {
        IsSuccess = false;
        // Note 3: This mirrors the success constructor: Value only has meaning when IsSuccess is true.
        Value = default!;
        Error = error;
    }

    /// <summary>Creates a successful result wrapping <paramref name="value"/>.</summary>
    public static Result<T> Success(T value) => new(value);

    /// <summary>Creates a failed result wrapping <paramref name="error"/>.</summary>
    public static Result<T> Failure(AppError error) => new(error);

    /// <summary>Implicit conversion from <typeparamref name="T"/> to a success result.</summary>
    public static implicit operator Result<T>(T value) => Success(value);

    /// <summary>Implicit conversion from <see cref="AppError"/> to a failure result.</summary>
    // Note 4: The implicit operators are what let handlers return either a response or an AppError directly.
    public static implicit operator Result<T>(AppError error) => Failure(error);
}
