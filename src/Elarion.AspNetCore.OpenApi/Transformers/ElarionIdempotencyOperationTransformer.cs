using System.Text.Json.Nodes;
using Elarion.Abstractions.Idempotency;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Elarion.AspNetCore.OpenApi.Transformers;

/// <summary>
/// Advertises the idempotency contract on operations whose handler is <c>[Idempotent]</c>: an optional
/// <c>Idempotency-Key</c> header parameter and the <c>x-elarion-idempotent</c> vendor extension. The signal is
/// the inert <see cref="ElarionIdempotentEndpointMetadata"/> the generator attaches to the endpoint, read here off
/// the operation's <c>ApiDescription</c>. Server-side enforcement is unaffected — it stays the pipeline
/// <c>IdempotencyDecorator</c> fed by <c>UseElarionIdempotencyKey</c>; this transformer only documents it.
/// </summary>
internal sealed class ElarionIdempotencyOperationTransformer : IOpenApiOperationTransformer {
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken) {
        var isIdempotent = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<ElarionIdempotentEndpointMetadata>()
            .Any();
        if (!isIdempotent) {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];
        var alreadyPresent = operation.Parameters.Any(parameter =>
            string.Equals(parameter.Name, IdempotencyKeyNames.HttpHeader, StringComparison.OrdinalIgnoreCase));
        if (!alreadyPresent) {
            operation.Parameters.Add(new OpenApiParameter {
                Name = IdempotencyKeyNames.HttpHeader,
                In = ParameterLocation.Header,
                Required = false,
                Description =
                    "Optional idempotency key. Repeating a request with the same key returns the original outcome "
                    + "instead of executing again, so a retry after a network failure is safe.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }

        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
        operation.Extensions[ElarionOpenApiExtensionNames.Idempotent] = new JsonNodeExtension(JsonValue.Create(true));

        return Task.CompletedTask;
    }
}
