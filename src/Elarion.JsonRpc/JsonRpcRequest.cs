using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elarion.JsonRpc;

public enum JsonRpcIdKind {
    Missing,
    Null,
    String,
    Number
}

public readonly record struct JsonRpcIdInfo(JsonRpcIdKind Kind, string? Value, string? Raw) {
    public bool HasId => Kind != JsonRpcIdKind.Missing;

    public static JsonRpcIdInfo Missing { get; } = new(JsonRpcIdKind.Missing, null, null);

    public static JsonRpcIdInfo Null { get; } = new(JsonRpcIdKind.Null, null, null);

    public static JsonRpcIdInfo String(string? value) {
        return new JsonRpcIdInfo(JsonRpcIdKind.String, value, value);
    }

    public static JsonRpcIdInfo Number(string raw) {
        return new JsonRpcIdInfo(JsonRpcIdKind.Number, raw, raw);
    }
}

/// <summary>
/// Represents a JSON-RPC 2.0 request envelope.
/// The <see cref="Id"/> field accepts both string and number values per the spec.
/// </summary>
[JsonConverter(typeof(JsonRpcRequestConverter))]
public sealed record JsonRpcRequest {
    private readonly string? _id;
    private readonly bool _hasId;
    private readonly JsonRpcIdKind _idKind;
    private readonly string? _idRaw;

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
    public string? Id {
        get => _id;
        init {
            _id = value;
            _hasId = true;
            _idKind = value is null ? JsonRpcIdKind.Null : JsonRpcIdKind.String;
            _idRaw = value;
        }
    }

    /// <summary>
    /// Whether the server should send a response for this request when it appears in a batch.
    /// Valid notifications, where the <c>id</c> member is absent, return <see langword="false"/>;
    /// explicit <c>"id": null</c> requests and invalid batch envelopes still require a response.
    /// </summary>
    [JsonIgnore]
    public bool ShouldSendResponse => _hasId || ForceResponse;

    internal bool HasId {
        get => _hasId;
        init => _hasId = value;
    }

    internal JsonRpcIdKind IdKind {
        get => _idKind;
        init => _idKind = value;
    }

    internal string? IdRaw {
        get => _idRaw;
        init => _idRaw = value;
    }

    internal int? BatchIndex { get; init; }

    internal int? BatchSize { get; init; }

    internal bool IsInvalidEnvelope { get; init; }

    internal bool ForceResponse { get; init; }
}

internal sealed class JsonRpcRequestConverter : JsonConverter<JsonRpcRequest> {
    public override JsonRpcRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("JSON-RPC request envelope must be an object.");

        string? jsonrpc = null;
        string? method = null;
        JsonElement? parameters = null;
        var id = JsonRpcIdInfo.Missing;

        while (reader.Read()) {
            if (reader.TokenType == JsonTokenType.EndObject)
                return new JsonRpcRequest {
                    Jsonrpc = jsonrpc ?? string.Empty,
                    Method = method ?? string.Empty,
                    Params = parameters,
                    Id = id.Value,
                    HasId = id.HasId,
                    IdKind = id.Kind,
                    IdRaw = id.Raw
                };

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("JSON-RPC request envelope property name expected.");

            var propertyName = reader.GetString();
            if (!reader.Read()) throw new JsonException("JSON-RPC request envelope ended unexpectedly.");

            switch (propertyName) {
                case "jsonrpc":
                    jsonrpc = reader.TokenType == JsonTokenType.String
                        ? reader.GetString()
                        : throw new JsonException("JSON-RPC protocol version must be a string.");
                    break;
                case "method":
                    method = reader.TokenType == JsonTokenType.String
                        ? reader.GetString()
                        : throw new JsonException("JSON-RPC method must be a string.");
                    break;
                case "params":
                    using (var parametersDocument = JsonDocument.ParseValue(ref reader)) {
                        parameters = parametersDocument.RootElement.Clone();
                    }

                    break;
                case "id":
                    id = ReadId(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("JSON-RPC request envelope ended unexpectedly.");
    }

    public override void Write(Utf8JsonWriter writer, JsonRpcRequest value, JsonSerializerOptions options) {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", value.Jsonrpc);
        writer.WriteString("method", value.Method);
        if (value.Params is { } parameters) {
            writer.WritePropertyName("params");
            parameters.WriteTo(writer);
        }

        if (value.HasId) {
            writer.WritePropertyName("id");
            JsonRpcIdWriter.Write(writer, value.IdKind, value.Id, value.IdRaw);
        }

        writer.WriteEndObject();
    }

    private static JsonRpcIdInfo ReadId(ref Utf8JsonReader reader) {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        return element.ValueKind switch {
            JsonValueKind.String => JsonRpcIdInfo.String(element.GetString()),
            JsonValueKind.Number => JsonRpcIdInfo.Number(element.GetRawText()),
            JsonValueKind.Null => JsonRpcIdInfo.Null,
            _ => throw new JsonException("JSON-RPC id must be a string, number, or null.")
        };
    }
}

internal static class JsonRpcIdWriter {
    public static void Write(Utf8JsonWriter writer, JsonRpcIdKind kind, string? value, string? raw) {
        switch (kind) {
            case JsonRpcIdKind.String:
                writer.WriteStringValue(value);
                break;
            case JsonRpcIdKind.Number when !string.IsNullOrWhiteSpace(raw):
                writer.WriteRawValue(raw);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
