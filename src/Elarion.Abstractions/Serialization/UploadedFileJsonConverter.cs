using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elarion.Abstractions.Serialization;

/// <summary>
/// The wire form of <see cref="UploadedFile"/> on every JSON surface: a plain JSON string, the blob id the upload
/// endpoint answered with. A string is what the upload endpoints return and what a client stores between the steps
/// of a multi-step flow, so the reference needs no envelope.
/// </summary>
public sealed class UploadedFileJsonConverter : JsonConverter<UploadedFile> {
    /// <inheritdoc />
    public override UploadedFile Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        // A wrong token must throw JsonException so transports classify it as invalid input (-32602), not an
        // internal error. Whether the id names an upload of the caller is IUploadedFiles' question, not the wire's.
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("An uploaded file must be a string: the id the upload endpoint answered with.");

        return new UploadedFile { Id = reader.GetString()! };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, UploadedFile value, JsonSerializerOptions options) {
        writer.WriteStringValue(value.Id);
    }
}
