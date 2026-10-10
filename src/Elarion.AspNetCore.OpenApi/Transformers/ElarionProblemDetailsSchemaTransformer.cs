using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Elarion.AspNetCore.OpenApi.Transformers;

/// <summary>
/// Documents the Elarion extension members of an RFC 7807 error body on the <see cref="ProblemDetails"/> schemas
/// (and every subtype, such as <c>HttpValidationProblemDetails</c>): the stable <c>code</c> string and the typed
/// <c>data</c> payload that <see cref="HttpAppErrorMapper.Extensions"/> writes (ADR-0080). Microsoft's schema only
/// knows the standard members, so without this an off-the-shelf client generator types the error contract's
/// discriminator as an unknown extension and a client cannot branch on it without a cast. Both members stay
/// optional: the component is shared by every operation in the document, including host-written routes whose
/// problems an Elarion translator did not produce. Existing properties are left untouched.
/// </summary>
internal sealed class ElarionProblemDetailsSchemaTransformer : IOpenApiSchemaTransformer {
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken) {
        if (context.JsonPropertyInfo is not null ||
            !typeof(ProblemDetails).IsAssignableFrom(context.JsonTypeInfo.Type))
            return Task.CompletedTask;

        schema.Properties ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        schema.Properties.TryAdd(HttpAppErrorMapper.CodeExtensionName, new OpenApiSchema {
            Type = JsonSchemaType.String,
            Description = "The stable, machine-readable error code (for example `not_found` or `seat.taken`)."
        });
        schema.Properties.TryAdd(HttpAppErrorMapper.DataExtensionName, new OpenApiSchema {
            Description = "The typed payload the error carries, when it has one; validation errors use `errors`."
        });

        return Task.CompletedTask;
    }
}
