using System.Text.Json;
using Elarion.JsonRpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
namespace Elarion.AspNetCore;

/// <summary>
/// Extension methods for registering JSON-RPC 2.0 services and endpoints.
/// </summary>
public static class JsonRpcServiceExtensions {
    /// <summary>
    /// Adds JSON-RPC 2.0 services to the DI container: options, batch strategy,
    /// and a <see cref="JsonSerializerOptions"/> singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Optional configuration delegate for <see cref="JsonRpcOptions"/>.
    /// Set <see cref="JsonRpcOptions.SerializerOptions"/> to provide a pre-built
    /// <see cref="JsonSerializerOptions"/> instance (recommended). When not set, a minimal
    /// default is built with camelCase naming and the <see cref="JsonRpcJsonContext"/> resolver.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// var serializerOptions = new JsonSerializerOptions { ... };
    ///
    /// // schema export
    /// JsonRpcSchemaExporter.Generate(dispatcher, serializerOptions);
    ///
    /// // runtime
    /// builder.Services.AddJsonRpc(o =&gt; o.SerializerOptions = serializerOptions);
    /// </code>
    /// </example>
    public static IServiceCollection AddJsonRpc(
        this IServiceCollection services,
        Action<JsonRpcOptions>? configure = null
    ) {
        if (configure is not null) {
            services.Configure(configure);
        } else {
            services.AddOptions<JsonRpcOptions>();
        }

        services.AddSingleton<IBatchExecutionStrategy, SequentialBatchStrategy>();

        return services;
    }

    /// <summary>
    /// Maps the JSON-RPC 2.0 POST endpoint at the path specified in <see cref="JsonRpcOptions.EndpointPath"/>.
    /// </summary>
    /// <param name="app">The endpoint route builder (typically <see cref="WebApplication"/>).</param>
    /// <returns>The route handler builder for further customization.</returns>
    /// <example>
    /// <code>
    /// var app = builder.Build();
    /// app.MapJsonRpc();
    /// </code>
    /// </example>
    public static IEndpointConventionBuilder MapJsonRpc(this IEndpointRouteBuilder app) {
        var options = app.ServiceProvider.GetRequiredService<IOptions<JsonRpcOptions>>().Value;
        return app.MapPost(options.EndpointPath, JsonRpcEndpoint.HandleRpc);
    }
}
