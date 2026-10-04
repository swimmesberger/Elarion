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
    /// <summary>The RFC 7807 extension member that carries <see cref="AppError.Code"/>; present on every error response.</summary>
    public const string CodeExtensionName = "code";

    /// <summary>The RFC 7807 extension member that carries the typed <see cref="AppError.Data"/> payload, when there is one.</summary>
    public const string DataExtensionName = "data";

    /// <summary>
    /// Returns the ProblemDetails extension members of <paramref name="error"/>: <c>code</c> always, plus <c>data</c>
    /// when the error carries a typed payload. A <see cref="ValidationErrorData"/> payload is not repeated as
    /// <c>data</c>: the validation problem already carries it as the standard <c>errors</c> map.
    /// </summary>
    public static IDictionary<string, object?> Extensions(AppError error) {
        var extensions = new Dictionary<string, object?> { [CodeExtensionName] = error.Code };
        if (error.Data is { } data and not ValidationErrorData) extensions[DataExtensionName] = data;

        return extensions;
    }

    /// <summary>Maps an <see cref="ErrorKind"/> to its HTTP status code.</summary>
    public static int MapToStatusCode(ErrorKind kind) {
        return kind switch {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorKind.BusinessRule => StatusCodes.Status422UnprocessableEntity,
            ErrorKind.Internal => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status500InternalServerError
        };
    }
}
