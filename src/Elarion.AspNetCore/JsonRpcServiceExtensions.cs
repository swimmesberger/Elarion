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
    /// <param name="configure">Optional configuration delegate for <see cref="JsonRpcOptions"/> (endpoint path,
    /// batch size). JSON serialization is configured centrally via <c>ConfigureElarionJson</c>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddElarionJsonRpc(
        this IServiceCollection services,
        Action<JsonRpcOptions>? configure = null
    ) {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<JsonRpcOptions>();

        services.AddSingleton<IBatchExecutionStrategy, SequentialBatchStrategy>();

        return services;
    }

    /// <summary>
    /// Adds JSON-RPC 2.0 services <em>and</em> registers the <see cref="JsonRpcDispatcher"/> singleton from the
    /// generated <paramref name="registerHandlers"/> map — the one-call setup for a JSON-RPC host. Serialization
    /// comes from the canonical <c>IElarionJsonSerialization</c> options (configure it via <c>ConfigureElarionJson</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="registerHandlers">The generated registration delegate (e.g. <c>ElarionBootstrapper.RegisterHandlers</c>).</param>
    /// <param name="configure">Optional additional <see cref="JsonRpcOptions"/> configuration (e.g. endpoint path).</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionJsonRpc(ElarionBootstrapper.RegisterHandlers);
    /// var app = builder.Build();
    /// app.MapElarionJsonRpc();
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionJsonRpc(
        this IServiceCollection services,
        Func<HandlerDispatcher, HandlerDispatcher> registerHandlers,
        Action<JsonRpcOptions>? configure = null
    ) {
        ArgumentNullException.ThrowIfNull(registerHandlers);

        services.AddElarionJsonRpcDispatcher(registerHandlers);

        return services.AddElarionJsonRpc(configure);
    }

    /// <summary>
    /// Adds JSON-RPC 2.0 services and registers the dispatcher from a <paramref name="register"/> delegate that also
    /// receives the application <see cref="IConfiguration"/> — the setup for a module-based host, where
    /// <c>ElarionBootstrapper.RegisterHandlers</c> registers only the operations of enabled modules.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="register">The registration delegate (e.g. <c>ElarionBootstrapper.RegisterHandlers</c>).</param>
    /// <param name="configure">Optional additional <see cref="JsonRpcOptions"/> configuration (e.g. endpoint path).</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionJsonRpc(ElarionBootstrapper.RegisterHandlers);
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionJsonRpc(
        this IServiceCollection services,
        Func<HandlerDispatcher, IConfiguration, HandlerDispatcher> register,
        Action<JsonRpcOptions>? configure = null
    ) {
        ArgumentNullException.ThrowIfNull(register);

        services.AddElarionJsonRpcDispatcher((dispatcher, sp) =>
            register(dispatcher, sp.GetRequiredService<IConfiguration>()));

        return services.AddElarionJsonRpc(configure);
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
