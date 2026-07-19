namespace Elarion.JsonRpc;

/// <summary>
/// Represents a JSON-RPC 2.0 error with a numeric code, message, and optional data.
/// Error codes follow the JSON-RPC 2.0 spec: -32700 (parse), -32600 (invalid request),
/// -32601 (method not found), -32602 (invalid params), -32603 (internal),
/// and -32000 to -32099 (server/application-defined).
/// </summary>
public sealed record RpcError {
    /// <summary>The integer error code per JSON-RPC 2.0.</summary>
    public required int Code { get; init; }

    /// <summary>A short human-readable description of the error.</summary>
    public required string Message { get; init; }

    /// <summary>Optional structured data providing additional context.</summary>
    public object? Data { get; init; }

    /// <summary>Parse error — invalid JSON received by the server (-32700).</summary>
    public static RpcError ParseError(string? message = null) {
        return new RpcError { Code = -32700, Message = message ?? "Parse error" };
    }

    /// <summary>Invalid request — the JSON is not a valid JSON-RPC 2.0 request (-32600).</summary>
    public static RpcError InvalidRequest(string? message = null) {
        return new RpcError { Code = -32600, Message = message ?? "Invalid request" };
    }

    /// <summary>Method not found — the requested method does not exist (-32601).</summary>
    public static RpcError MethodNotFound(string? message = null) {
        return new RpcError { Code = -32601, Message = message ?? "Method not found" };
    }

    /// <summary>Invalid params — invalid method parameters (-32602).</summary>
    public static RpcError InvalidParams(string? message = null) {
        return new RpcError { Code = -32602, Message = message ?? "Invalid params" };
    }

    /// <summary>Internal error — an unexpected server error (-32603).</summary>
    public static RpcError InternalError(string? message = null) {
        return new RpcError { Code = -32603, Message = message ?? "Internal error" };
    }
}
