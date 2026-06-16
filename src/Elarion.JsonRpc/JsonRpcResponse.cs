namespace Elarion.JsonRpc;

/// <summary>
/// Represents a JSON-RPC 2.0 response envelope.
/// Always returned with HTTP 200 — errors are conveyed in the body per spec.
/// </summary>
public sealed class JsonRpcResponse {
    /// <summary>JSON-RPC protocol version, always "2.0".</summary>
    public string Jsonrpc { get; init; } = "2.0";

    /// <summary>The request id echoed back. Null for parse errors.</summary>
    public string? Id { get; init; }

    /// <summary>The handler result. Present only on success.</summary>
    public object? Result { get; init; }

    /// <summary>The error object. Present only on failure.</summary>
    public RpcErrorResponse? Error { get; init; }

    /// <summary>Creates a success response wrapping the handler result.</summary>
    public static JsonRpcResponse Success(string? id, object? result) =>
        new() { Id = id, Result = result };

    /// <summary>Creates an error response from an <see cref="RpcError"/>.</summary>
    public static JsonRpcResponse FromError(string? id, RpcError error) =>
        new() { Id = id, Error = new RpcErrorResponse { Code = error.Code, Message = error.Message, Data = error.Data } };

    /// <summary>Creates a "method not found" error response (JSON-RPC -32601).</summary>
    public static JsonRpcResponse MethodNotFound(string? id) =>
        new() { Id = id, Error = new RpcErrorResponse { Code = -32601, Message = "Method not found" } };

    /// <summary>Creates a "parse error" response (JSON-RPC -32700, no id available).</summary>
    public static JsonRpcResponse ParseError() =>
        new() { Error = new RpcErrorResponse { Code = -32700, Message = "Parse error" } };

    /// <summary>Creates an "invalid request" error response (JSON-RPC -32600).</summary>
    public static JsonRpcResponse InvalidRequest(string? id) =>
        new() { Id = id, Error = new RpcErrorResponse { Code = -32600, Message = "Invalid request" } };
}

/// <summary>
/// The error object within a JSON-RPC 2.0 error response.
/// </summary>
public sealed record RpcErrorResponse {
    /// <summary>The integer error code per JSON-RPC 2.0.</summary>
    public required int Code { get; init; }
    /// <summary>A short human-readable description of the error.</summary>
    public required string Message { get; init; }
    /// <summary>Optional structured data providing additional context.</summary>
    public object? Data { get; init; }
}
