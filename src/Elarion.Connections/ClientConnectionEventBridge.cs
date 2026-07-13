using System.Collections.Concurrent;
using Elarion.Abstractions.Connections;
using Elarion.Abstractions.Dispatch;
using Elarion.ClientEvents;
using Elarion.Connections.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Connections;

/// <summary>
/// The client-events bridge: makes any connection adapter a peer delivery leg of the SSE endpoint.
/// A subscribe call runs the same transport-neutral pipeline (<see cref="ClientEventSubscriptionResolver"/> —
/// same topic catalog, same fail-closed authorization) inside a dispatch scope seeded with the connection's
/// principal, registers with the same in-process subscriber registry (so the greeting/interest lifecycle and
/// any cross-node broadcaster reach these subscribers unchanged), and pumps matched envelopes into the
/// adapter's <see cref="ClientEventDelivery"/>. Registered as an <see cref="IClientConnectionObserver"/>,
/// it disposes a connection's subscriptions when the connection unregisters.
/// </summary>
/// <remarks>
/// Requires <c>AddElarionClientEvents</c>; without it <see cref="SubscribeAsync"/> fails loud at resolution.
/// Principal seeding rides the standard dispatch rail — the host's registered
/// <see cref="IDispatchScopeInitializer"/>s (e.g. the framework current-user initializer) turn the captured
/// <see cref="System.Security.Claims.ClaimsPrincipal"/> into the scope's <c>ICurrentUser</c>, exactly as the
/// JSON-RPC and MCP transports do per call.
/// </remarks>
public sealed class ClientConnectionEventBridge(
    IServiceProvider services,
    ILogger<ClientConnectionEventBridge>? logger = null) : IClientConnectionObserver {
    private readonly ILogger<ClientConnectionEventBridge> _logger =
        logger ?? NullLogger<ClientConnectionEventBridge>.Instance;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ClientConnectionEventSubscription, byte>> _byConnection =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves <paramref name="requests"/> for <paramref name="connection"/>'s principal and, on success,
    /// starts delivering matched envelopes (greeting first) through <paramref name="deliver"/> until the
    /// returned subscription is disposed or the connection unregisters.
    /// </summary>
    /// <param name="connection">The connection subscribing (its principal authorizes the requests, its id
    /// ties the subscription's lifetime).</param>
    /// <param name="requests">The requested subscriptions, as parsed by the adapter.</param>
    /// <param name="deliver">The adapter's framing callback.</param>
    /// <param name="ct">A cancellation token observed during resolution.</param>
    public async ValueTask<ClientConnectionEventSubscribeResult> SubscribeAsync(
        ClientConnection connection,
        IReadOnlyList<ClientEventSubscriptionRequest> requests,
        ClientEventDelivery deliver,
        CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(deliver);

        ClientEventSubscriptionResolution resolution;
        var boundary = new DispatchScopeContext();
        boundary.Set(connection.Principal);
        await using (var scope = services.CreateDispatchScope(boundary)) {
            var resolver = scope.ServiceProvider.GetRequiredService<ClientEventSubscriptionResolver>();
            resolution = await resolver.ResolveAsync(requests, ct);
        }

        if (resolution.Status != ClientEventSubscriptionStatus.Resolved) {
            return new ClientConnectionEventSubscribeResult { Status = resolution.Status };
        }

        var source = services.GetRequiredService<IClientEventSubscriptionSource>();
        var connectionId = connection.ConnectionId;
        var subscriptions = _byConnection.GetOrAdd(connectionId, static _ => new ConcurrentDictionary<ClientConnectionEventSubscription, byte>());
        var subscription = new ClientConnectionEventSubscription(
            source.Subscribe(resolution.Subscriptions), deliver,
            onFinished: self => Untrack(connectionId, self), _logger);
        subscriptions.TryAdd(subscription, 0);
        ConnectionTelemetry.ActiveEventSubscriptions.Add(1);

        // A disconnect can race the subscribe: OnDisconnectedAsync may have swept the set between GetOrAdd
        // and TryAdd. Detect it and end the newborn subscription instead of leaking a pump on a dead link.
        if (!_byConnection.TryGetValue(connectionId, out var current) || !ReferenceEquals(current, subscriptions)) {
            subscription.Dispose();
        }
        else {
            subscription.Start();
        }

        return new ClientConnectionEventSubscribeResult {
            Status = ClientEventSubscriptionStatus.Resolved,
            Subscription = subscription,
        };
    }

    /// <inheritdoc />
    public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(connection);
        if (_byConnection.TryRemove(connection.ConnectionId, out var subscriptions)) {
            foreach (var subscription in subscriptions.Keys) {
                subscription.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    private void Untrack(string connectionId, ClientConnectionEventSubscription subscription) {
        // Runs exactly once per subscription (the subscription's dispose is once-guarded), so the gauge
        // stays balanced even on the subscribe-vs-disconnect race path.
        ConnectionTelemetry.ActiveEventSubscriptions.Add(-1);
        if (_byConnection.TryGetValue(connectionId, out var subscriptions)) {
            subscriptions.TryRemove(subscription, out _);
            if (subscriptions.IsEmpty) {
                // Drop the empty set so a connection id never leaves a permanent entry behind (e.g. a
                // subscribe issued after the connection already unregistered). Pair-conditioned removal:
                // a concurrent subscribe that re-created or grabbed the set is protected by the
                // ReferenceEquals re-check in SubscribeAsync.
                _byConnection.TryRemove(
                    new KeyValuePair<string, ConcurrentDictionary<ClientConnectionEventSubscription, byte>>(
                        connectionId, subscriptions));
            }
        }
    }
}
