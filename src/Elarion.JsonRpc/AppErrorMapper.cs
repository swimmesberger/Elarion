using Elarion.Abstractions;

namespace Elarion.JsonRpc;

/// <summary>
/// Maps the framework's transport-agnostic <see cref="AppError"/> / <see cref="ErrorKind"/> onto JSON-RPC 2.0
/// error codes. Protocol-level kinds reuse the spec codes (e.g. invalid params, internal error); the remaining
/// application kinds use the reserved JSON-RPC server range (-32000 to -32099).
/// </summary>
/// <remarks>
/// This is the framework's default mapping, used by <see cref="JsonRpcDispatcher"/> via
/// <see cref="JsonRpcAppErrorTranslator"/>. Applications needing different codes can register their own
/// <c>IAppErrorTranslator&lt;RpcError&gt;</c> to override it.
/// </remarks>
public static class AppErrorMapper {
    /// <summary>Converts an <see cref="AppError"/> to a JSON-RPC <see cref="RpcError"/>.</summary>
    public static RpcError ToRpcError(AppError error) =>
        new() { Code = MapToCode(error.Kind), Message = error.Message, Data = error.Data };

    /// <summary>Maps an <see cref="ErrorKind"/> to its JSON-RPC integer error code.</summary>
    /// <remarks>
    /// The application-range codes (-32001..-32005) are a wire contract mirrored by the TypeScript client
    /// generator (<c>src/elarion-jsonrpc-client-generator/src/rpc-client-source.ts</c>, <c>ElarionErrorCodes</c>
    /// and the <c>RpcError</c> getters). Keep both in sync — changing a code breaks every generated client.
    /// </remarks>
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
