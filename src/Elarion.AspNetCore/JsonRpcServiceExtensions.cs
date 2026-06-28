using System.Text.Json;
using Elarion.Abstractions.Dispatch;
using Elarion.JsonRpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
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
    /// builder.Services.AddElarionJsonRpc(o =&gt; o.SerializerOptions = serializerOptions);
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionJsonRpc(
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
    /// Adds JSON-RPC 2.0 services <em>and</em> registers the <see cref="JsonRpcDispatcher"/> singleton from the
    /// generated <paramref name="registerAll"/> map — the one-call setup for a JSON-RPC host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="serializerOptions">The serializer options used by the dispatcher and the endpoint.</param>
    /// <param name="registerHandlers">The generated registration delegate (e.g. <c>ModuleBootstrapper.RegisterHandlers</c>).</param>
    /// <param name="configure">Optional additional <see cref="JsonRpcOptions"/> configuration (e.g. endpoint path).</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionJsonRpc(serializerOptions, ModuleBootstrapper.RegisterHandlers);
    /// var app = builder.Build();
    /// app.MapElarionJsonRpc();
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionJsonRpc(
        this IServiceCollection services,
        JsonSerializerOptions serializerOptions,
        Func<HandlerDispatcher, HandlerDispatcher> registerHandlers,
        Action<JsonRpcOptions>? configure = null
    ) {
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(registerHandlers);

        services.AddElarionJsonRpcDispatcher(serializerOptions, registerHandlers);

        return services.AddElarionJsonRpc(options => {
            options.SerializerOptions = serializerOptions;
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Adds JSON-RPC 2.0 services and registers the dispatcher from a <paramref name="register"/> delegate that also
    /// receives the application <see cref="IConfiguration"/> — the setup for a module-based host, where
    /// <c>ModuleBootstrapper.RegisterHandlers</c> registers only the operations of enabled modules.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="serializerOptions">The serializer options used by the dispatcher and the endpoint.</param>
    /// <param name="register">The registration delegate (e.g. <c>ModuleBootstrapper.RegisterHandlers</c>).</param>
    /// <param name="configure">Optional additional <see cref="JsonRpcOptions"/> configuration (e.g. endpoint path).</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionJsonRpc(serializerOptions, ModuleBootstrapper.RegisterHandlers);
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionJsonRpc(
        this IServiceCollection services,
        JsonSerializerOptions serializerOptions,
        Func<HandlerDispatcher, IConfiguration, HandlerDispatcher> register,
        Action<JsonRpcOptions>? configure = null
    ) {
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(register);

        services.AddElarionJsonRpcDispatcher(
            serializerOptions,
            (dispatcher, sp) => register(dispatcher, sp.GetRequiredService<IConfiguration>()));

        return services.AddElarionJsonRpc(options => {
            options.SerializerOptions = serializerOptions;
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Maps the JSON-RPC 2.0 POST endpoint at the path specified in <see cref="JsonRpcOptions.EndpointPath"/>.
    /// </summary>
    /// <param name="app">The endpoint route builder (typically <see cref="WebApplication"/>).</param>
    /// <returns>The route handler builder for further customization.</returns>
    /// <example>
    /// <code>
    /// var app = builder.Build();
    /// app.MapElarionJsonRpc();
    /// </code>
    /// </example>
    public static IEndpointConventionBuilder MapElarionJsonRpc(this IEndpointRouteBuilder app) {
        var options = app.ServiceProvider.GetRequiredService<IOptions<JsonRpcOptions>>().Value;
        return app.MapPost(options.EndpointPath, JsonRpcEndpoint.HandleRpc);
    }
}
