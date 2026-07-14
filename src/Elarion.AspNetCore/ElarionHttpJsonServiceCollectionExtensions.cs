using Elarion.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Elarion.AspNetCore;

/// <summary>
/// Aligns ASP.NET Core's minimal-API JSON options (<see cref="HttpJsonOptions"/>) with Elarion's canonical
/// <see cref="IElarionJsonSerialization"/> configuration, so the <c>[HttpEndpoint]</c> transport (de)serializes
/// through the same source-generated contexts every other transport uses.
/// </summary>
/// <remarks>
/// <para>
/// The <c>[HttpEndpoint]</c> transport serializes <b>both directions</b> through <see cref="HttpJsonOptions"/>:
/// request-body binding (a POST/PUT/PATCH JSON body), the success value (<see cref="ElarionHttpResults"/> uses
/// <see cref="Microsoft.AspNetCore.Http.TypedResults"/>), and OpenAPI schema generation all read those options,
/// which Elarion otherwise leaves at ASP.NET defaults. With <c>JsonSerializerIsReflectionEnabledByDefault=false</c>
/// (the repo/AOT default) that means the request/response types have <b>no resolver</b> and fail to (de)serialize.
/// Calling <see cref="AddElarionHttpJson"/> fixes that for every host mapping <c>[HttpEndpoint]</c> handlers — it
/// is not specific to OpenAPI (<c>AddElarionOpenApi</c> calls it for you). By aligning to the canonical config it
/// also keeps REST output identical to the JSON-RPC/MCP transports for the same DTO.
/// </para>
/// <para>
/// It also registers ASP.NET's ProblemDetails services (<c>AddProblemDetails()</c>): the RFC 7807 error legs
/// (<see cref="ElarionHttpResults.ToProblem"/> via <c>Results.Problem</c>/<c>Results.ValidationProblem</c>)
/// serialize through ASP.NET's own source-generated <c>ProblemDetailsJsonContext</c>, which only that call
/// contributes — without it, every <see cref="Elarion.Abstractions.AppError"/> response 500s under
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c>. A host's own <c>AddProblemDetails(configure)</c>
/// composes with this registration in either order.
/// </para>
/// <para>
/// This is a deliberate, global alignment: it changes <see cref="HttpJsonOptions"/> for the whole app, so a
/// host's own hand-written minimal-API endpoints share Elarion's canonical JSON contract (camelCase, source-gen
/// resolvers, ignore-null-when-writing). Because the aligning configuration runs in registration order, a host
/// that needs different behavior calls <c>services.ConfigureHttpJsonOptions(…)</c> <b>after</b>
/// <see cref="AddElarionHttpJson"/> and wins. The call is idempotent.
/// </para>
/// </remarks>
public static class ElarionHttpJsonServiceCollectionExtensions {
    /// <summary>
    /// Aligns <see cref="HttpJsonOptions"/> with the canonical <see cref="IElarionJsonSerialization"/> options.
    /// Idempotent; safe to call from both the host and <c>AddElarionOpenApi</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddElarionHttpJson(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElarionJson();
        // The ElarionHttpResults error legs render through Results.Problem/Results.ValidationProblem, whose
        // ProblemDetails payloads resolve only via ASP.NET's ProblemDetailsJsonContext — contributed by
        // AddProblemDetails(). Without it every AppError response 500s with reflection off. TryAdd-based, so a
        // host's own AddProblemDetails(configure) composes in either order.
        services.AddProblemDetails();
        // TryAddEnumerable dedupes by implementation type, so repeated calls register the aligner exactly once.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<HttpJsonOptions>, ElarionHttpJsonConfigureOptions>());

        return services;
    }
}

/// <summary>
/// Copies the canonical Elarion JSON knobs and source-generated resolver chain onto the ASP.NET Core
/// minimal-API JSON options. Runs as an ordinary <see cref="IConfigureOptions{TOptions}"/> so any host
/// <c>ConfigureHttpJsonOptions</c> registered afterwards composes on top of it.
/// </summary>
internal sealed class ElarionHttpJsonConfigureOptions(IElarionJsonSerialization serialization)
    : IConfigureOptions<HttpJsonOptions> {
    public void Configure(HttpJsonOptions options) {
        var canonical = serialization.Options;
        var target = options.SerializerOptions;

        target.PropertyNamingPolicy = canonical.PropertyNamingPolicy;
        target.PropertyNameCaseInsensitive = canonical.PropertyNameCaseInsensitive;
        target.DefaultIgnoreCondition = canonical.DefaultIgnoreCondition;

        // Prepend the canonical resolvers so they win first-match over any ASP.NET default resolver, preserving
        // their internal order. The frozen canonical options are only read here.
        var index = 0;
        foreach (var resolver in canonical.TypeInfoResolverChain) {
            if (!target.TypeInfoResolverChain.Contains(resolver)) {
                target.TypeInfoResolverChain.Insert(index++, resolver);
            }
        }
    }
}
