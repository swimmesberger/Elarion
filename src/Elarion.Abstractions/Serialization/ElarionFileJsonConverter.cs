using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elarion.Abstractions.Serialization;

/// <summary>
/// The canonical JSON envelope for <see cref="ElarionFile"/>, used wherever a file payload crosses a JSON
/// surface — a JSON-RPC result or params property, an MCP tool result, an HTTP JSON body, an
/// idempotency/cache replay row: <c>{ "contentType", "fileName"?, "data" (base64) }</c>. The property names
/// are a fixed wire contract (camelCase, like the JSON-RPC envelope itself) and do not follow the host's
/// naming policy; unknown properties are skipped on read.
/// </summary>
public sealed class ElarionFileJsonConverter : JsonConverter<ElarionFile> {
    public override ElarionFile Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartObject) {
            throw new JsonException("Expected an object for an ElarionFile payload.");
        }

        string? contentType = null;
        string? fileName = null;
        byte[]? data = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
            if (reader.ValueTextEquals("contentType"u8)) {
                reader.Read();
                contentType = reader.GetString();
            } else if (reader.ValueTextEquals("fileName"u8)) {
                reader.Read();
                fileName = reader.GetString();
            } else if (reader.ValueTextEquals("data"u8)) {
                reader.Read();
                data = reader.GetBytesFromBase64();
            } else {
                reader.Read();
                reader.Skip();
            }
        }

        if (contentType is null || contentType.Length == 0) {
            throw new JsonException("An ElarionFile payload requires a non-empty 'contentType'.");
        }

        if (data is null) {
            throw new JsonException("An ElarionFile payload requires a base64 'data' property.");
        }

        return new ElarionFile(data, contentType) { FileName = fileName };
    }

    public override void Write(Utf8JsonWriter writer, ElarionFile value, JsonSerializerOptions options) {
        writer.WriteStartObject();
        writer.WriteString("contentType"u8, value.ContentType);
        if (value.FileName is not null) {
            writer.WriteString("fileName"u8, value.FileName);
        }

        writer.WriteBase64String("data"u8, value.Bytes.Span);
        writer.WriteEndObject();
    }
}
