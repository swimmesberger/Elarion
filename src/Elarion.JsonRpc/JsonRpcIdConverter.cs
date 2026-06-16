using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elarion.JsonRpc;

/// <summary>
/// Accepts both string and number JSON-RPC 2.0 id values.
/// Per the spec, id may be a string, a number, or null.
/// Numbers are converted to their string representation so the rest of the code
/// can treat id as <see cref="string"/> uniformly.
/// </summary>
internal sealed class JsonRpcIdConverter : JsonConverter<string?> {
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch {
            JsonTokenType.String => reader.GetString(),
            // Numbers are stored as their decimal representation (e.g. "42" or "1.5")
            JsonTokenType.Number => reader.TryGetInt64(out var n) ? n.ToString() : reader.GetDouble().ToString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token type for JSON-RPC id: {reader.TokenType}"),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) {
        if (value is null) {
            writer.WriteNullValue();
        } else {
            writer.WriteStringValue(value);
        }
    }
}
