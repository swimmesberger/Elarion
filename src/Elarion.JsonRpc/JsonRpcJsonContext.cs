using System.Text.Json.Serialization;

namespace Elarion.JsonRpc;

/// <summary>
/// Source-generated JSON serializer context for JSON-RPC 2.0 envelope types.
/// App-level types (handler DTOs) are kept in the consumer's own context and
/// combined at runtime via <see cref="System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver"/>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(RpcErrorResponse))]
[JsonSerializable(typeof(List<JsonRpcResponse>), TypeInfoPropertyName = "ListOfJsonRpcResponse")]
public sealed partial class JsonRpcJsonContext : JsonSerializerContext;
