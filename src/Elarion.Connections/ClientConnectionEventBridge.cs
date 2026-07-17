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
    private readonly ConcurrentDictionary<string, ConnectionSubscriptions> _byConnection =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Captures <paramref name="connection"/>'s current snapshot, resolves <paramref name="requests"/> under
    /// that principal, and on success starts delivering matched envelopes (greeting first) until the returned
    /// subscription is disposed, the identity changes, or the connection unregisters.
    /// </summary>
    /// <param name="connection">The live connection sink. A detached identity snapshot is insufficient because
    /// subscription registration must synchronize with promotion and disconnect.</param>
    /// <param name="requests">The requested subscriptions, as parsed by the adapter.</param>
    /// <param name="deliver">The adapter's framing callback.</param>
    /// <param name="ct">A cancellation token observed during resolution.</param>
    public async ValueTask<ClientConnectionEventSubscribeResult> SubscribeAsync(
        IClientConnectionSink connection,
        IReadOnlyList<ClientEventSubscriptionRequest> requests,
        ClientEventDelivery deliver,
        CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(deliver);

        var snapshot = connection.Connection;
        ClientEventSubscriptionResolution resolution;
        var boundary = new DispatchScopeContext();
        boundary.Set(snapshot.Principal);
        await using (var scope = services.CreateDispatchScope(boundary)) {
            var resolver = scope.ServiceProvider.GetRequiredService<ClientEventSubscriptionResolver>();
            resolution = await resolver.ResolveAsync(requests, ct);
        }

        if (resolution.Status != ClientEventSubscriptionStatus.Resolved) {
            return new ClientConnectionEventSubscribeResult { Status = resolution.Status };
        }

        var source = services.GetRequiredService<IClientEventSubscriptionSource>();
        var connectionId = snapshot.ConnectionId;
        var subscriptions = _byConnection.GetOrAdd(
            connectionId, _ => new ConnectionSubscriptions(connection.ConnectionState));
        var subscription = new ClientConnectionEventSubscription(
            source.Subscribe(resolution.Subscriptions),
            (envelope, deliveryCt) => DeliverIfCurrentAsync(
                connection.ConnectionState, snapshot.IdentityRevision, deliver, envelope, deliveryCt),
            onFinished: self => Untrack(connectionId, subscriptions, self), _logger);
        bool start;
        lock (subscriptions.Gate) {
            subscriptions.Items.TryAdd(subscription, 0);
            ConnectionTelemetry.ActiveEventSubscriptions.Add(1);
            start = connection.ConnectionState.IsCurrent(snapshot.IdentityRevision);
        }

        if (start) {
            subscription.Start();
        }
        else {
            subscription.Dispose();
        }

        return new ClientConnectionEventSubscribeResult {
            Status = ClientEventSubscriptionStatus.Resolved,
            Subscription = subscription,
        };
    }

    /// <inheritdoc />
    public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(connection);
        _byConnection.TryAdd(
            connection.Connection.ConnectionId,
            new ConnectionSubscriptions(connection.ConnectionState));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(connection);
        CloseSubscriptions(connection.ConnectionId);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnIdentityPromotedAsync(
        ClientConnection previous,
        ClientConnection current,
        CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        InvalidateSubscriptions(previous.ConnectionId);
        return ValueTask.CompletedTask;
    }

    private static ValueTask DeliverIfCurrentAsync(
        ClientConnectionState state,
        long identityRevision,
        ClientEventDelivery deliver,
        ClientEventEnvelope envelope,
        CancellationToken ct) {
        if (!state.IsCurrent(identityRevision)) {
            throw new ClientConnectionClosedException(state.Current.ConnectionId);
        }

        return deliver(envelope, ct);
    }

    private void InvalidateSubscriptions(string connectionId) {
        if (!_byConnection.TryGetValue(connectionId, out var subscriptions)) {
            return;
        }

        DisposeTracked(subscriptions);
    }

    private void CloseSubscriptions(string connectionId) {
        if (_byConnection.TryRemove(connectionId, out var subscriptions)) {
            DisposeTracked(subscriptions);
        }
    }

    private static void DisposeTracked(ConnectionSubscriptions subscriptions) {
        ClientConnectionEventSubscription[] dispose;
        lock (subscriptions.Gate) {
            dispose = [.. subscriptions.Items.Keys];
            subscriptions.Items.Clear();
        }

        foreach (var subscription in dispose) {
            subscription.Dispose();
        }
    }

    private void Untrack(
        string connectionId,
        ConnectionSubscriptions subscriptions,
        ClientConnectionEventSubscription subscription) {
        // Runs exactly once per subscription (the subscription's dispose is once-guarded), so the gauge
        // stays balanced even on promotion/disconnect races.
        ConnectionTelemetry.ActiveEventSubscriptions.Add(-1);
        lock (subscriptions.Gate) {
            subscriptions.Items.TryRemove(subscription, out _);
            if (subscriptions.Items.IsEmpty && !subscriptions.ConnectionIsRegistered) {
                _byConnection.TryRemove(new KeyValuePair<string, ConnectionSubscriptions>(connectionId, subscriptions));
            }
        }
    }

    private sealed class ConnectionSubscriptions(ClientConnectionState state) {
        public Lock Gate { get; } = new();
        public ConcurrentDictionary<ClientConnectionEventSubscription, byte> Items { get; } = [];
        public bool ConnectionIsRegistered => state.IsRegistered;
    }
}
