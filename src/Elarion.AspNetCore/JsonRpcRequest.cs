using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elarion.AspNetCore;

/// <summary>
/// Represents a JSON-RPC 2.0 request envelope.
/// The <see cref="Id"/> field accepts both string and number values per the spec.
/// </summary>
public sealed record JsonRpcRequest {
    /// <summary>JSON-RPC protocol version, always "2.0".</summary>
    public required string Jsonrpc { get; init; }
    /// <summary>The RPC method name to invoke.</summary>
    public required string Method { get; init; }
    /// <summary>
    /// Optional parameters for the method. Per JSON-RPC 2.0 §4.2 the field MAY be omitted;
    /// absent is treated identically to null/empty.
    /// </summary>
    public JsonElement? Params { get; init; }
    /// <summary>
    /// The request identifier; absent means the request is a notification (no response expected).
    /// Accepts both string and number values per the spec.
    /// </summary>
    [JsonConverter(typeof(JsonRpcIdConverter))]
    public string? Id { get; init; }
}
