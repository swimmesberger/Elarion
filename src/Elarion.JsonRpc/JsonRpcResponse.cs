using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elarion.JsonRpc;

/// <summary>
/// Represents a JSON-RPC 2.0 response envelope.
/// Always returned with HTTP 200 — errors are conveyed in the body per spec.
/// </summary>
[JsonConverter(typeof(JsonRpcResponseConverter))]
public sealed class JsonRpcResponse {
    private readonly string? _id;
    private readonly JsonRpcIdKind _idKind;
    private readonly string? _idRaw;

    /// <summary>JSON-RPC protocol version, always "2.0".</summary>
    public string Jsonrpc { get; init; } = "2.0";

    /// <summary>The request id echoed back. Null for parse errors.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Id {
        get => _id;
        init {
            _id = value;
            _idKind = value is null ? JsonRpcIdKind.Null : JsonRpcIdKind.String;
            _idRaw = value;
        }
    }

    /// <summary>The handler result. Present only on success.</summary>
    public object? Result { get; init; }

    /// <summary>The error object. Present only on failure.</summary>
    public RpcErrorResponse? Error { get; init; }

    /// <summary>Creates a success response wrapping the handler result.</summary>
    public static JsonRpcResponse Success(string? id, object? result) {
        return new JsonRpcResponse { Id = id, Result = result };
    }

    internal static JsonRpcResponse Success(JsonRpcRequest request, object? result) {
        return new JsonRpcResponse {
            Id = request.Id,
            IdKind = request.IdKind,
            IdRaw = request.IdRaw,
            Result = result
        };
    }

    /// <summary>Creates an error response from an <see cref="RpcError"/>.</summary>
    public static JsonRpcResponse FromError(string? id, RpcError error) {
        return new JsonRpcResponse
            { Id = id, Error = new RpcErrorResponse { Code = error.Code, Message = error.Message, Data = error.Data } };
    }

    internal static JsonRpcResponse FromError(JsonRpcRequest request, RpcError error) {
        return new JsonRpcResponse {
            Id = request.Id,
            IdKind = request.IdKind,
            IdRaw = request.IdRaw,
            Error = new RpcErrorResponse { Code = error.Code, Message = error.Message, Data = error.Data }
        };
    }

    /// <summary>Creates a "method not found" error response (JSON-RPC -32601).</summary>
    public static JsonRpcResponse MethodNotFound(string? id) {
        return new JsonRpcResponse
            { Id = id, Error = new RpcErrorResponse { Code = -32601, Message = "Method not found" } };
    }

    internal static JsonRpcResponse MethodNotFound(JsonRpcRequest request) {
        return new JsonRpcResponse {
            Id = request.Id,
            IdKind = request.IdKind,
            IdRaw = request.IdRaw,
            Error = new RpcErrorResponse { Code = -32601, Message = "Method not found" }
        };
    }

    /// <summary>Creates a "parse error" response (JSON-RPC -32700, no id available).</summary>
    public static JsonRpcResponse ParseError() {
        return new JsonRpcResponse { Error = new RpcErrorResponse { Code = -32700, Message = "Parse error" } };
    }

    /// <summary>Creates an "invalid request" error response (JSON-RPC -32600).</summary>
    public static JsonRpcResponse InvalidRequest(string? id) {
        return new JsonRpcResponse
            { Id = id, Error = new RpcErrorResponse { Code = -32600, Message = "Invalid request" } };
    }

    internal static JsonRpcResponse InvalidRequest(JsonRpcRequest request) {
        return new JsonRpcResponse {
            Id = request.Id,
            IdKind = request.IdKind,
            IdRaw = request.IdRaw,
            Error = new RpcErrorResponse { Code = -32600, Message = "Invalid request" }
        };
    }

    internal JsonRpcIdKind IdKind {
        get => _idKind;
        init => _idKind = value;
    }

    internal string? IdRaw {
        get => _idRaw;
        init => _idRaw = value;
    }
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

internal sealed class JsonRpcResponseConverter : JsonConverter<JsonRpcResponse> {
    public override JsonRpcResponse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        throw new NotSupportedException("JSON-RPC responses are serialized by the server, not deserialized.");
    }

    public override void Write(Utf8JsonWriter writer, JsonRpcResponse value, JsonSerializerOptions options) {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", value.Jsonrpc);

        writer.WritePropertyName("id");
        JsonRpcIdWriter.Write(writer, value.IdKind, value.Id, value.IdRaw);

        if (value.Error is not null) {
            writer.WritePropertyName("error");
            JsonSerializer.Serialize(writer, value.Error, options.GetTypeInfo(typeof(RpcErrorResponse)));
        }
        else {
            writer.WritePropertyName("result");
            if (value.Result is null)
                writer.WriteNullValue();
            else
                // Serialize by the runtime result type, but resolve its contract through the configured
                // (source-gen) resolver so this stays reflection-free and Native-AOT-safe.
                JsonSerializer.Serialize(writer, value.Result, options.GetTypeInfo(value.Result.GetType()));
        }

        writer.WriteEndObject();
    }
}
