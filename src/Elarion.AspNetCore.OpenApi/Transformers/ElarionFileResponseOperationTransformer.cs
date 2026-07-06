using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Elarion.AspNetCore.OpenApi.Transformers;

/// <summary>
/// Documents the success response of a file-returning <c>[HttpEndpoint]</c> (a handler responding with
/// <c>Result&lt;ElarionFile&gt;</c>) as a binary payload: <c>application/octet-stream</c> with
/// <c>type: string, format: binary</c>, so off-the-shelf client generators produce a blob/stream return
/// instead of an empty object. The signal is the inert <see cref="ElarionFileEndpointMetadata"/> the generator
/// attaches to the endpoint; the concrete content type served at run time comes from each
/// <c>ElarionFile.ContentType</c> and may be more specific than the advertised generic binary type.
/// </summary>
internal sealed class ElarionFileResponseOperationTransformer : IOpenApiOperationTransformer {
    private const string BinaryContentType = "application/octet-stream";

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken) {
        var isFileResponse = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<ElarionFileEndpointMetadata>()
            .Any();
        if (!isFileResponse) {
            return Task.CompletedTask;
        }

        operation.Responses ??= [];
        if (!operation.Responses.TryGetValue("200", out var existing) || existing is not OpenApiResponse response) {
            response = new OpenApiResponse { Description = "OK" };
            operation.Responses["200"] = response;
        }

        response.Content ??= new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal);
        response.Content[BinaryContentType] = new OpenApiMediaType {
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" },
        };

        return Task.CompletedTask;
    }
}
