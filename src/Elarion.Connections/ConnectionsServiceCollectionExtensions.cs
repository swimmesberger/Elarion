using Elarion.Abstractions.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Connections;

/// <summary>Registers the connection kernel: the node-local registry and the client-events bridge.</summary>
public static class ConnectionsServiceCollectionExtensions {
    /// <summary>
    /// Adds the connection kernel services (idempotent; later <paramref name="configure"/> delegates
    /// compose onto the same <see cref="ElarionConnectionsOptions"/> instance). Adapters resolve
    /// <see cref="IClientConnectionRegistry"/> to report lifecycle edges and
    /// <see cref="ClientConnectionEventBridge"/> to serve client-event subscriptions; hosts add their own
    /// <see cref="IClientConnectionObserver"/>s with <c>TryAddEnumerable</c>. Client-event bridging
    /// additionally requires <c>AddElarionClientEvents</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional kernel-wide connection options (e.g.
    /// <see cref="ElarionConnectionsOptions.DefaultInvokeTimeout"/>).</param>
    public static IServiceCollection AddElarionConnections(
        this IServiceCollection services, Action<ElarionConnectionsOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        // Compose repeat calls onto the one instance this method registered. Keyed descriptors are
        // skipped (reading ImplementationInstance on them throws); a host-registered non-instance
        // descriptor fails loud instead of being silently shadowed by a fresh default.
        var descriptor = services.LastOrDefault(candidate =>
            candidate.ServiceType == typeof(ElarionConnectionsOptions) && !candidate.IsKeyedService);
        if (descriptor is not null && descriptor.ImplementationInstance is not ElarionConnectionsOptions) {
            throw new InvalidOperationException(
                "ElarionConnectionsOptions is already registered with a factory or implementation type. "
                + "Configure connections via AddElarionConnections(options => …) instead of registering the options yourself.");
        }

        var existing = descriptor?.ImplementationInstance as ElarionConnectionsOptions;
        var options = existing ?? new ElarionConnectionsOptions();
        configure?.Invoke(options);
        if (options.DefaultInvokeTimeout is { } timeout
            && timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) {
            throw new ArgumentException(
                "DefaultInvokeTimeout must be positive, Timeout.InfiniteTimeSpan, or null.", nameof(configure));
        }

        if (existing is null) {
            services.AddSingleton(options);
        }

        services.TryAddSingleton<IClientConnectionRegistry, ClientConnectionRegistry>();
        services.TryAddSingleton<ClientConnectionEventBridge>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IClientConnectionObserver, ClientConnectionEventBridgeObserver>());
        return services;
    }
}

/// <summary>
/// Forwards lifecycle edges to the singleton bridge. A separate type (rather than registering the bridge
/// itself) so <c>TryAddEnumerable</c> can deduplicate by implementation type and the observer resolves the
/// same instance adapters use for <see cref="ClientConnectionEventBridge.SubscribeAsync"/>.
/// </summary>
internal sealed class ClientConnectionEventBridgeObserver(ClientConnectionEventBridge bridge) : IClientConnectionObserver {
    public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) =>
        bridge.OnConnectedAsync(connection, ct);

    public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) =>
        bridge.OnDisconnectedAsync(connection, ct);
}
