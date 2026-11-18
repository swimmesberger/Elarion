using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Elarion.Typst;

/// <summary>
/// The JSON document a compilation writes to <c>/data.json</c> - the template's input model.
/// </summary>
/// <remarks>
/// The template model is its own contract, not the handler's wire DTO: it carries what the layout prints, already
/// formatted where formatting is a business decision (amounts, dates in the document's locale). Serialize it with a
/// source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> so the path stays trimming-
/// and AOT-safe; naming policy and null handling are the context's choice. Typst's <c>json()</c> turns <c>null</c>
/// into <c>none</c>, so <c>data.at("field", default: …)</c> covers both an omitted and a null field.
/// </remarks>
/// <example>
/// <code>
/// [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
///     DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
/// [JsonSerializable(typeof(InvoiceDocument))]
/// internal sealed partial class DocumentJsonContext : JsonSerializerContext;
///
/// var data = TypstData.FromValue(document, DocumentJsonContext.Default.InvoiceDocument);
/// </code>
/// </example>
public sealed class TypstData {
    private readonly Func<Stream, CancellationToken, Task> _write;

    private TypstData(Func<Stream, CancellationToken, Task> write) => _write = write;

    /// <summary>Serializes <paramref name="value"/> with <paramref name="typeInfo"/> straight into <c>data.json</c>.</summary>
    public static TypstData FromValue<T>(T value, JsonTypeInfo<T> typeInfo) {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new TypstData((stream, cancellationToken) => JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken));
    }

    /// <summary>Uses an already serialized JSON document.</summary>
    public static TypstData FromJson(string json) {
        ArgumentNullException.ThrowIfNull(json);
        return FromUtf8Json(System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Uses an already serialized UTF-8 JSON document.</summary>
    public static TypstData FromUtf8Json(ReadOnlyMemory<byte> utf8Json) =>
        new((stream, cancellationToken) => stream.WriteAsync(utf8Json, cancellationToken).AsTask());

    internal Task WriteToAsync(Stream stream, CancellationToken cancellationToken) => _write(stream, cancellationToken);
}
