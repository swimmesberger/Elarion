using System.Text.Json.Nodes;
using Elarion.Abstractions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Elarion.AspNetCore.OpenApi.Transformers;

/// <summary>
/// Documents an <see cref="UploadedFile"/> as what it is on the wire: a string, the blob id of a file staged through
/// the upload endpoints (ADR-0105). The type serializes through its own converter, so the schema generator would
/// otherwise describe it as an opaque schema that accepts anything. The schema is marked
/// <see cref="ElarionOpenApiExtensionNames.Upload"/>, the keyword the JSON-RPC schema export uses, so a client
/// generator can upload a file there first; the nullability the generator derived is kept.
/// </summary>
internal sealed class ElarionUploadedFileSchemaTransformer : IOpenApiSchemaTransformer {
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken) {
        if (context.JsonTypeInfo.Type != typeof(UploadedFile))
            return Task.CompletedTask;

        schema.Type = schema.Type is { } type && type.HasFlag(JsonSchemaType.Null)
            ? JsonSchemaType.String | JsonSchemaType.Null
            : JsonSchemaType.String;
        schema.Properties?.Clear();
        schema.Description ??= "A file staged through the upload endpoint; the value is the blob id it answered with.";
        schema.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
        schema.Extensions[ElarionOpenApiExtensionNames.Upload] = new JsonNodeExtension(JsonValue.Create(true));
        return Task.CompletedTask;
    }
}
