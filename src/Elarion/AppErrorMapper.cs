using Elarion.Abstractions;
using Elarion.JsonRpc;

namespace Elarion;

/// <summary>
/// Maps the framework's transport-agnostic <see cref="AppError"/> / <see cref="ErrorKind"/> onto JSON-RPC 2.0
/// error codes. Protocol-level kinds reuse the spec codes (e.g. invalid params, internal error); the remaining
/// application kinds use the reserved JSON-RPC server range (-32000 to -32099).
/// </summary>
/// <remarks>
/// This is the framework's default mapping, used by <see cref="RpcDispatcherExtensions.MapHandler{TRequest,TResponse}"/>.
/// Applications needing different codes can register methods via the raw
/// <see cref="JsonRpcDispatcher.Map{TRequest,TResponse}"/> API and supply their own mapping.
/// </remarks>
public static class AppErrorMapper {
    /// <summary>Converts an <see cref="AppError"/> to a JSON-RPC <see cref="RpcError"/>.</summary>
    public static RpcError ToRpcError(AppError error) =>
        new() { Code = MapToCode(error.Kind), Message = error.Message, Data = error.Data };

    /// <summary>Maps an <see cref="ErrorKind"/> to its JSON-RPC integer error code.</summary>
    public static int MapToCode(ErrorKind kind) => kind switch {
        ErrorKind.Validation => -32602,   // Invalid params
        ErrorKind.NotFound => -32001,
        ErrorKind.Conflict => -32002,
        ErrorKind.Forbidden => -32003,
        ErrorKind.BusinessRule => -32004,
        ErrorKind.Unauthorized => -32005,
        ErrorKind.Internal => -32603,      // Internal error
        _ => -32603,
    };
}
