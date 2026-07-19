using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Elarion.AspNetCore.OpenApi.Transformers;

/// <summary>
/// Sets the JSON Schema <c>enum</c> keyword on property schemas carrying <see cref="AllowedValuesAttribute"/>.
/// Microsoft's built-in DataAnnotations→schema mapping omits <c>[AllowedValues]</c>; the JSON-RPC schema
/// exporter emits <c>enum</c> for it (ADR-0027), so this transformer keeps the OpenAPI document and the
/// RPC/MCP schemas in agreement — a declared value set (e.g. a configuration-variant vocabulary) then reaches
/// every generated client. The attribute is read through the schema's
/// <see cref="System.Text.Json.Serialization.Metadata.JsonPropertyInfo.AttributeProvider"/>, the same channel
/// Microsoft's own mapping uses, so it works under the repo's reflection-off source-generated resolver chain.
/// A <c>null</c> allowed value is omitted here (OpenAPI carries nullability on the schema type); an existing
/// non-empty <c>enum</c> is left untouched so a future built-in mapping wins.
/// </summary>
internal sealed class ElarionAllowedValuesSchemaTransformer : IOpenApiSchemaTransformer {
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken) {
        if (context.JsonPropertyInfo?.AttributeProvider is { } provider &&
            provider.GetCustomAttributes(typeof(AllowedValuesAttribute), false)
                is [AllowedValuesAttribute allowedValues, ..] &&
            schema.Enum is not { Count: > 0 }) {
            var nodes = new List<JsonNode>();
            foreach (var value in allowedValues.Values)
                if (ToJsonNode(value) is { } node)
                    nodes.Add(node);

            schema.Enum = nodes;
        }

        return Task.CompletedTask;
    }

    private static JsonNode? ToJsonNode(object? value) {
        return value switch {
            null => null,
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            short number => JsonValue.Create(number),
            byte number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            float number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            char character => JsonValue.Create(character.ToString()),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
        };
    }
}
