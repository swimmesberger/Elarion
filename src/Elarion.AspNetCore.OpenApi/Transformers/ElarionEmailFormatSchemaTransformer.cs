using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Elarion.AspNetCore.OpenApi.Transformers;

/// <summary>
/// Sets <c>format: "email"</c> on property schemas carrying <see cref="EmailAddressAttribute"/>. Microsoft's
/// built-in DataAnnotations→schema mapping covers <c>[Range]</c>, the length attributes,
/// <c>[RegularExpression]</c>, <c>[Url]</c>, and <c>[Base64String]</c> but omits <c>[EmailAddress]</c>; the
/// JSON-RPC schema exporter emits <c>format: "email"</c> for it (ADR-0027), so this transformer keeps the OpenAPI
/// document and the RPC/MCP schemas in agreement. The attribute is read through the schema's
/// <see cref="System.Text.Json.Serialization.Metadata.JsonPropertyInfo.AttributeProvider"/>, the same channel
/// Microsoft's own mapping uses, so it works under the repo's reflection-off source-generated resolver chain.
/// </summary>
internal sealed class ElarionEmailFormatSchemaTransformer : IOpenApiSchemaTransformer {
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken) {
        if (context.JsonPropertyInfo?.AttributeProvider is { } provider &&
            provider.GetCustomAttributes(typeof(EmailAddressAttribute), inherit: false) is { Length: > 0 }) {
            schema.Format = "email";
        }

        return Task.CompletedTask;
    }
}
