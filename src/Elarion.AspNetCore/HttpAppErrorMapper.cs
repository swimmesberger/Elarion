using Elarion.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Elarion.AspNetCore;

/// <summary>
/// Maps the framework's transport-agnostic <see cref="AppError"/> / <see cref="ErrorKind"/> onto HTTP status
/// codes. This is the HTTP counterpart to <c>Elarion.AppErrorMapper</c> (which maps the same kinds onto
/// JSON-RPC error codes), and is used by <see cref="ElarionHttpResults"/> when translating a failed
/// <see cref="Result{T}"/> into an RFC 7807 ProblemDetails response.
/// </summary>
public static class HttpAppErrorMapper {
    /// <summary>Maps an <see cref="ErrorKind"/> to its HTTP status code.</summary>
    public static int MapToStatusCode(ErrorKind kind) => kind switch {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        ErrorKind.Internal => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status500InternalServerError,
    };
}
