using Elarion.AspNetCore.OpenApi.Transformers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AspNetCore.OpenApi;

/// <summary>
/// Registers OpenAPI document generation for Elarion <c>[HttpEndpoint]</c> handlers on top of
/// <c>Microsoft.AspNetCore.OpenApi</c>. This is the opt-in sibling that brings the REST transport to
/// schema/contract parity with JSON-RPC: it wires the canonical Elarion JSON serialization into the OpenAPI
/// schema pipeline (so body schemas resolve through the same source-generated contexts every other transport
/// uses, with reflection off), keeps the module tags the generator emits, normalizes operation ids, and
/// advertises the <c>Idempotency-Key</c> contract for <c>[Idempotent]</c> handlers.
/// </summary>
/// <remarks>
/// Call <c>app.MapOpenApi()</c> (from <c>Microsoft.AspNetCore.OpenApi</c>) to serve the generated document, or add
/// <c>Microsoft.Extensions.ApiDescription.Server</c> and set <c>&lt;OpenApiGenerateDocuments&gt;true&lt;/…&gt;</c>
/// to emit it at build time — the REST analog of the JSON-RPC <c>rpc-schema.json</c> export.
/// </remarks>
public static class ElarionOpenApiServiceCollectionExtensions {
    /// <summary>Adds the default Elarion OpenAPI document (unnamed, served at <c>/openapi/v1.json</c>).</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional extra configuration applied after the Elarion transformers.</param>
    public static IServiceCollection AddElarionOpenApi(
        this IServiceCollection services,
        Action<OpenApiOptions>? configureOptions = null) =>
        services.AddElarionOpenApi(documentName: "v1", configureOptions);

    /// <summary>Adds an Elarion OpenAPI document with the given <paramref name="documentName"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="documentName">The OpenAPI document name (served at <c>/openapi/{documentName}.json</c>).</param>
    /// <param name="configureOptions">Optional extra configuration applied after the Elarion transformers.</param>
    public static IServiceCollection AddElarionOpenApi(
        this IServiceCollection services,
        string documentName,
        Action<OpenApiOptions>? configureOptions = null) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(documentName);

        // Align ASP.NET's HTTP JSON options with the canonical Elarion serialization — the base HTTP transport
        // wiring that makes schema generation (and request-body binding) resolve through the source-gen contexts
        // with reflection off. Idempotent, and shared with any [HttpEndpoint] host that calls it directly.
        services.AddElarionHttpJson();

        services.AddOpenApi(documentName, options => {
            // OperationId cleanup has full-document visibility (it de-collides), so it is a document transformer.
            options.AddDocumentTransformer<ElarionOperationIdDocumentTransformer>();
            // Idempotency is per-operation: it reads the generator's endpoint marker off the ApiDescription.
            options.AddOperationTransformer<ElarionIdempotencyOperationTransformer>();
            configureOptions?.Invoke(options);
        });

        return services;
    }
}
