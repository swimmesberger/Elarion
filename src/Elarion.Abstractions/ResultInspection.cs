using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Elarion.Abstractions;

/// <summary>
/// Inspects a response of an unconstrained type for the result pattern (<see cref="IResultLike"/>,
/// <see cref="IResultError"/>) without allocating. Use it in decorators and other generic code instead of an inline
/// <c>response is IResultLike { IsSuccess: false }</c> check.
/// </summary>
/// <remarks>
/// For a struct response such as <see cref="Result{T}"/>, an interface pattern on a generic value is a box that the
/// JIT normally removes. Inside a method compiled with .NET 11 runtime async (<c>runtime-async=on</c>) it currently
/// does not, so the inline check allocates the boxed result on every call. These helpers are deliberately never
/// inlined: compiled on their own, specialized for the struct, the box is removed again under either async model.
/// A response that does not implement the result pattern is neither a success nor a failure.
/// </remarks>
/// <example>
/// <code>
/// var response = await inner.HandleAsync(request, ct).ConfigureAwait(false);
/// if (ResultInspection.IsSuccess(response)) await unitOfWork.CommitAsync(ct);
/// </code>
/// </example>
public static class ResultInspection {
    /// <summary>True when <paramref name="response"/> implements <see cref="IResultLike"/> and succeeded.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsSuccess<T>(T response) {
        return response is IResultLike { IsSuccess: true };
    }

    /// <summary>True when <paramref name="response"/> implements <see cref="IResultLike"/> and failed.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsFailure<T>(T response) {
        return response is IResultLike { IsSuccess: false };
    }

    /// <summary>
    /// Gets the <see cref="AppError"/> of a failed <paramref name="response"/> that implements
    /// <see cref="IResultError"/>; <see langword="false"/> for a success or a response without that contract.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryGetError<T>(T response, [NotNullWhen(true)] out AppError? error) {
        if (response is IResultError { IsSuccess: false, Error: { } failure }) {
            error = failure;
            return true;
        }

        error = null;
        return false;
    }

    /// <summary>
    /// Creates a failed <typeparamref name="T"/> carrying <paramref name="error"/> when <typeparamref name="T"/> is one of
    /// the result types (<see cref="Result"/>, <see cref="Result{T}"/>); <see langword="false"/> for any other type. For
    /// generic code whose response type is not constrained to <see cref="IResultFailureFactory{TSelf}"/>, such as a
    /// decorator that attaches to every command.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryCreateFailure<T>(AppError error, [MaybeNullWhen(false)] out T failure) {
        ArgumentNullException.ThrowIfNull(error);
        // The result types are structs, so default(T) is an instance to ask; a reference type answers null and fails.
        if (default(T) is IResultFailureSource<T> source) {
            failure = source.CreateFailure(error);
            return true;
        }

        failure = default;
        return false;
    }
}
