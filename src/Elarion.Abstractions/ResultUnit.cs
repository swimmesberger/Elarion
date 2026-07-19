using Elarion.Abstractions.Results;

namespace Elarion.Abstractions;

/// <summary>
/// A value-type result that represents success without a value, or an <see cref="AppError"/>.
/// It is the non-generic companion to <see cref="Result{T}"/> for operations that produce no
/// response — most notably the <see cref="IHandler{T}"/> convenience interface, whose
/// <c>HandleAsync</c> returns this type. It converts to <see cref="Result{T}"/> of
/// <see cref="Unit"/> so it never enters the generic decorator/dispatch pipeline.
/// </summary>
/// <example>
/// <code>
/// // Returning success
/// return Result.Success();
///
/// // Returning failure via implicit conversion
/// return AppError.NotFound("Project not found");
/// </code>
/// </example>
public readonly struct Result : IResultLike, IResultError, IResultFailureFactory<Result> {
    /// <inheritdoc />
    public bool IsSuccess { get; }

    /// <summary>
    /// The error. Only valid when <see cref="IsSuccess"/> is <c>false</c>. A <c>default</c>-initialized
    /// result (which is a failure but carries no error) yields <see cref="ResultDefaults.UninitializedError"/>
    /// instead of <see langword="null"/>, so transports translating <see cref="Error"/> never hit a null error.
    /// </summary>
    public AppError Error => _error ?? (IsSuccess ? default! : ResultDefaults.UninitializedError);

    private readonly AppError? _error;

    private Result(bool isSuccess, AppError? error) {
        IsSuccess = isSuccess;
        _error = error;
    }

    /// <summary>Creates a successful result.</summary>
    public static Result Success() {
        return new Result(true, null);
    }

    /// <summary>Creates a failed result wrapping <paramref name="error"/>.</summary>
    public static Result Failure(AppError error) {
        return new Result(false, error);
    }

    /// <summary>Implicit conversion from <see cref="AppError"/> to a failure result.</summary>
    public static implicit operator Result(AppError error) {
        return Failure(error);
    }

    /// <summary>
    /// Converts to the generic <see cref="Result{T}"/> of <see cref="Unit"/>, preserving
    /// success/failure. This is what bridges the <see cref="IHandler{T}"/> surface onto the
    /// generic handler pipeline.
    /// </summary>
    public Result<Unit> ToResultUnit() {
        return IsSuccess ? Result<Unit>.Success(Unit.Value) : Result<Unit>.Failure(Error);
    }

    /// <summary>Implicit conversion to <see cref="Result{T}"/> of <see cref="Unit"/>.</summary>
    public static implicit operator Result<Unit>(Result result) {
        return result.ToResultUnit();
    }
}
