namespace Elarion.JsonRpc;

/// <summary>
/// The <c>error.data</c> member of every JSON-RPC error this transport writes: the stable machine-readable error
/// <see cref="Code"/> plus the optional typed <see cref="Data"/> payload. The numeric JSON-RPC <c>error.code</c>
/// stays the coarse <see cref="Elarion.Abstractions.ErrorKind"/> mapping; clients branch on <see cref="Code"/>.
/// </summary>
/// <remarks>
/// Wire shape: <c>{ "code": "token.malformed", "data": { ... } }</c>, <c>data</c> omitted when the error has no
/// payload. Protocol-level errors carry the codes in <see cref="RpcErrorCodes"/>. <see cref="Data"/> is serialized
/// by its runtime type, so the type must be registered in a source-generated JSON context like any other payload.
/// </remarks>
public sealed record RpcErrorData {
    /// <summary>The stable machine-readable error code.</summary>
    public required string Code { get; init; }

    /// <summary>The typed error payload, or <see langword="null"/> (omitted on the wire) when there is none.</summary>
    public object? Data { get; init; }
}

/// <summary>The stable error codes of JSON-RPC protocol-level failures (not produced by handlers).</summary>
public static class RpcErrorCodes {
    /// <summary>Invalid JSON was received (-32700).</summary>
    public const string ParseError = "parse_error";

    /// <summary>The JSON is not a valid JSON-RPC 2.0 request (-32600).</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>The requested method does not exist (-32601).</summary>
    public const string MethodNotFound = "method_not_found";

    /// <summary>The method parameters could not be bound (-32602).</summary>
    public const string InvalidParams = "invalid_params";
}
