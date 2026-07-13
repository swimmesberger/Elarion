using Elarion.Abstractions.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Connections;

/// <summary>Registers the connection kernel: the node-local registry and the client-events bridge.</summary>
public static class ConnectionsServiceCollectionExtensions {
    /// <summary>
    /// Adds the connection kernel services (idempotent). Adapters resolve
    /// <see cref="IClientConnectionRegistry"/> to report lifecycle edges and
    /// <see cref="ClientConnectionEventBridge"/> to serve client-event subscriptions; hosts add their own
    /// <see cref="IClientConnectionObserver"/>s with <c>TryAddEnumerable</c>. Client-event bridging
    /// additionally requires <c>AddElarionClientEvents</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddElarionConnections(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);
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
