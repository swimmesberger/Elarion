using System.Text.Json;
using Elarion.Abstractions.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Elarion.JsonRpc;

/// <summary>
/// DI registration for the transport-neutral <see cref="HandlerDispatcher"/> (the named bus) and the JSON-RPC
/// adapter (<see cref="JsonRpcDispatcher"/>) over it.
/// </summary>
public static class JsonRpcDispatcherServiceExtensions {
    /// <summary>
    /// Registers the shared <see cref="HandlerDispatcher"/> singleton, built once from the generated
    /// <paramref name="registerHandlers"/> map and frozen. Idempotent (<c>TryAddSingleton</c>), so every transport
    /// host that needs the bus (JSON-RPC, MCP, …) can call it with the same delegate and the registry is built once.
    /// </summary>
    /// <remarks>
    /// <b>First registration wins.</b> Pass the <b>same</b> registration delegate (e.g.
    /// <c>ElarionBootstrapper.RegisterHandlers</c>) to every transport — <c>AddElarionJsonRpc</c> and
    /// <c>AddElarionMcp</c> both route through this method, and a divergent second delegate is silently ignored
    /// because the bus is a single shared singleton.
    /// </remarks>
    public static IServiceCollection AddElarionHandlerDispatcher(
        this IServiceCollection services,
        Func<HandlerDispatcher, HandlerDispatcher> registerHandlers) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registerHandlers);

        services.TryAddSingleton(_ => registerHandlers(new HandlerDispatcher()).Freeze());
        return services;
    }

    /// <summary>
    /// Registers the shared <see cref="HandlerDispatcher"/> singleton using a registration delegate that also
    /// receives the <see cref="IServiceProvider"/>, so registration can resolve services (e.g. configuration) at
    /// compose time — for example to gate operations by a per-module feature flag. The transport-neutral
    /// <c>Elarion.JsonRpc</c> package stays free of a configuration dependency; ASP.NET hosts get an
    /// <c>IConfiguration</c>-flavored overload from <c>Elarion.AspNetCore</c>.
    /// </summary>
    /// <remarks><b>First registration wins</b> — see the other overload; pass the same delegate to every transport.</remarks>
    public static IServiceCollection AddElarionHandlerDispatcher(
        this IServiceCollection services,
        Func<HandlerDispatcher, IServiceProvider, HandlerDispatcher> registerHandlers) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registerHandlers);

        services.TryAddSingleton(sp => registerHandlers(new HandlerDispatcher(), sp).Freeze());
        return services;
    }

    /// <summary>
    /// Registers the shared <see cref="HandlerDispatcher"/> and a singleton <see cref="JsonRpcDispatcher"/> adapter
    /// over it, using <paramref name="serializerOptions"/> for params/result handling.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionJsonRpcDispatcher(serializerOptions, ElarionBootstrapper.RegisterHandlers);
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionJsonRpcDispatcher(
        this IServiceCollection services,
        JsonSerializerOptions serializerOptions,
        Func<HandlerDispatcher, HandlerDispatcher> registerHandlers) {
        ArgumentNullException.ThrowIfNull(serializerOptions);

        services.AddElarionHandlerDispatcher(registerHandlers);
        services.AddElarionJsonRpcAdapter(serializerOptions);
        return services;
    }

    /// <summary>
    /// Registers the shared <see cref="HandlerDispatcher"/> (config-aware) and a singleton
    /// <see cref="JsonRpcDispatcher"/> adapter over it.
    /// </summary>
    public static IServiceCollection AddElarionJsonRpcDispatcher(
        this IServiceCollection services,
        JsonSerializerOptions serializerOptions,
        Func<HandlerDispatcher, IServiceProvider, HandlerDispatcher> registerHandlers) {
        ArgumentNullException.ThrowIfNull(serializerOptions);

        services.AddElarionHandlerDispatcher(registerHandlers);
        services.AddElarionJsonRpcAdapter(serializerOptions);
        return services;
    }

    private static void AddElarionJsonRpcAdapter(this IServiceCollection services, JsonSerializerOptions serializerOptions) =>
        services.TryAddSingleton(sp => new JsonRpcDispatcher(
            sp.GetRequiredService<HandlerDispatcher>(),
            serializerOptions,
            sp.GetService<ILogger<JsonRpcDispatcher>>()));
}
