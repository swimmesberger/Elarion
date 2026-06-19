using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.JsonRpc;

/// <summary>
/// DI registration for the <see cref="JsonRpcDispatcher"/>.
/// </summary>
public static class JsonRpcDispatcherServiceExtensions {
    /// <summary>
    /// Registers a singleton <see cref="JsonRpcDispatcher"/> built from <paramref name="serializerOptions"/> and
    /// the generated <paramref name="registerAll"/> map, frozen and ready for dispatch. The dispatcher is created
    /// lazily on first resolution so its <see cref="ILogger{TCategoryName}"/> is taken from the built container —
    /// no separate eager instance or logger-attaching copy is needed.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="serializerOptions">The serializer options the dispatcher uses for params/result handling.</param>
    /// <param name="registerAll">
    /// The generated registration delegate (e.g. <c>RpcMethodMap.RegisterAll</c>) that maps all handlers onto the
    /// dispatcher and returns it for chaining.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionJsonRpcDispatcher(serializerOptions, RpcMethodMap.RegisterAll);
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionJsonRpcDispatcher(
        this IServiceCollection services,
        JsonSerializerOptions serializerOptions,
        Func<JsonRpcDispatcher, JsonRpcDispatcher> registerAll) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(registerAll);

        services.AddSingleton(sp =>
            registerAll(new JsonRpcDispatcher(serializerOptions, sp.GetService<ILogger<JsonRpcDispatcher>>()))
                .Freeze());

        return services;
    }

    /// <summary>
    /// Registers the dispatcher singleton using a registration delegate that also receives the
    /// <see cref="IServiceProvider"/>, so registration can resolve services (e.g. configuration) at compose time —
    /// for example to gate methods by a per-module feature flag. The transport-neutral
    /// <c>Elarion.JsonRpc</c> package stays free of a configuration dependency; ASP.NET hosts get an
    /// <c>IConfiguration</c>-flavored overload from <c>Elarion.AspNetCore</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="serializerOptions">The serializer options the dispatcher uses for params/result handling.</param>
    /// <param name="registerAll">The registration delegate, given the dispatcher and the request <see cref="IServiceProvider"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddElarionJsonRpcDispatcher(
        this IServiceCollection services,
        JsonSerializerOptions serializerOptions,
        Func<JsonRpcDispatcher, IServiceProvider, JsonRpcDispatcher> registerAll) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(registerAll);

        services.AddSingleton(sp =>
            registerAll(new JsonRpcDispatcher(serializerOptions, sp.GetService<ILogger<JsonRpcDispatcher>>()), sp)
                .Freeze());

        return services;
    }
}
