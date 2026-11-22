using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
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
/// <remarks>
/// <para>
/// A member's declared <see cref="UploadLimitsAttribute"/> (ADR-0100) is written as
/// <see cref="ElarionOpenApiExtensionNames.MaxBytes"/> and <see cref="ElarionOpenApiExtensionNames.ContentTypes"/>,
/// placed as the JSON-RPC schema places them. Every other <see cref="UploadedFile"/> shares one component schema, which
/// a per-member limit must never change, so a member with declared limits keeps its own inline schema: its schema id
/// is dropped before the document turns schemas into component references.
/// </para>
/// <para>
/// A collection of <see cref="UploadedFile"/> has no <c>items</c> in the generated schema (the element serializes
/// through a converter that accepts anything); they are put back as the marked string, carrying the member's limits.
/// </para>
/// </remarks>
internal sealed class ElarionUploadedFileSchemaTransformer : IOpenApiSchemaTransformer {
    // The metadata key under which Microsoft.AspNetCore.OpenApi records a schema's component id; a schema without it
    // stays inline. Pinned by ElarionOpenApiEndToEndTests, which fails if the key ever changes.
    private const string SchemaIdMetadataKey = "x-schema-id";

    private const string Description = "A file staged through the upload endpoint; the value is the blob id it answered with.";

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken) {
        if (context.JsonTypeInfo.Type == typeof(UploadedFile)) {
            schema.Type = schema.Type is { } type && type.HasFlag(JsonSchemaType.Null)
                ? JsonSchemaType.String | JsonSchemaType.Null
                : JsonSchemaType.String;
            schema.Properties?.Clear();
            schema.Description ??= Description;
            schema.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
            schema.Extensions[ElarionOpenApiExtensionNames.Upload] = new JsonNodeExtension(JsonValue.Create(true));

            if (FindUploadLimits(context.JsonPropertyInfo) is { } limits && AddLimits(schema, limits))
                schema.Metadata?.Remove(SchemaIdMetadataKey);

            return Task.CompletedTask;
        }

        if (context.JsonTypeInfo is { Kind: JsonTypeInfoKind.Enumerable } collection
            && collection.ElementType == typeof(UploadedFile)
            && schema.Items is null) {
            var items = new OpenApiSchema {
                Type = JsonSchemaType.String,
                Description = Description,
                Extensions = new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal) {
                    [ElarionOpenApiExtensionNames.Upload] = new JsonNodeExtension(JsonValue.Create(true))
                }
            };
            if (FindUploadLimits(context.JsonPropertyInfo) is { } limits) AddLimits(items, limits);

            schema.Items = items;
        }

        return Task.CompletedTask;
    }

    // Writes the declared limits; false when the attribute declares none (the build rejects that, ELUPL001).
    private static bool AddLimits(OpenApiSchema schema, UploadLimitsAttribute limits) {
        var constraints = limits.ToConstraints();
        if (constraints.MaxBytes is null && constraints.ContentTypes is null) return false;

        schema.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
        if (constraints.MaxBytes is { } maxBytes)
            schema.Extensions[ElarionOpenApiExtensionNames.MaxBytes] = new JsonNodeExtension(JsonValue.Create(maxBytes));
        if (constraints.ContentTypes is { } contentTypes)
            schema.Extensions[ElarionOpenApiExtensionNames.ContentTypes] = new JsonNodeExtension(
                new JsonArray(contentTypes.Select(static t => (JsonNode?)JsonValue.Create(t)).ToArray()));

        return true;
    }

    // [UploadLimits] on the member, or on the positional record's constructor parameter, as the JSON-RPC schema reads it.
    private static UploadLimitsAttribute? FindUploadLimits(JsonPropertyInfo? property) {
        return Find(property?.AttributeProvider) ?? Find(property?.AssociatedParameter?.AttributeProvider);
    }

    private static UploadLimitsAttribute? Find(ICustomAttributeProvider? provider) {
        return provider?.GetCustomAttributes(typeof(UploadLimitsAttribute), true) is [UploadLimitsAttribute found, ..]
            ? found
            : null;
    }
}
