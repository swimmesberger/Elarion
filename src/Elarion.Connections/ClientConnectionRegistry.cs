using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Elarion.Abstractions.Connections;
using Elarion.Connections.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Connections;

/// <summary>
/// The node-local registry default: a concurrent index of this instance's live connections, doubling as the
/// lifecycle broker — observers are notified after the index mutation (a connect observer already sees the
/// connection in lookups, a disconnect observer no longer does), and an observer failure is logged, never
/// propagated, so one faulty observer can neither tear down a connection nor starve its peers.
/// </summary>
internal sealed class ClientConnectionRegistry(
    IEnumerable<IClientConnectionObserver> observers,
    ILogger<ClientConnectionRegistry>? logger = null) : IClientConnectionRegistry {
    private readonly IClientConnectionObserver[] _observers = [.. observers];
    private readonly ILogger<ClientConnectionRegistry> _logger =
        logger ?? NullLogger<ClientConnectionRegistry>.Instance;
    private readonly ConcurrentDictionary<string, IClientConnectionSink> _connections = new(StringComparer.Ordinal);

    public async ValueTask RegisterAsync(IClientConnectionSink connection, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(connection);
        var connectionId = connection.Connection.ConnectionId;
        if (!_connections.TryAdd(connectionId, connection)) {
            throw new InvalidOperationException(
                $"A connection with id '{connectionId}' is already registered. Connection ids are unique per node by contract — a duplicate registration is an adapter bug, not a race to tolerate.");
        }

        ConnectionTelemetry.RecordOpened(connection.Connection.Transport);
        foreach (var observer in _observers) {
            try {
                await observer.OnConnectedAsync(connection, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            }
            catch (Exception failure) {
                _logger.LogWarning(failure,
                    "Client-connection observer {Observer} failed on connect of {ConnectionId}.",
                    observer.GetType().Name, connectionId);
            }
        }
    }

    public async ValueTask UnregisterAsync(string connectionId, CancellationToken ct = default) {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        if (!_connections.TryRemove(connectionId, out var removed)) {
            return;
        }

        ConnectionTelemetry.RecordClosed(removed.Connection.Transport);
        foreach (var observer in _observers) {
            try {
                await observer.OnDisconnectedAsync(removed.Connection, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            }
            catch (Exception failure) {
                _logger.LogWarning(failure,
                    "Client-connection observer {Observer} failed on disconnect of {ConnectionId}.",
                    observer.GetType().Name, connectionId);
            }
        }
    }

    public bool TryGet(string connectionId, [NotNullWhen(true)] out IClientConnectionSink? connection) {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        return _connections.TryGetValue(connectionId, out connection);
    }

    public IReadOnlyList<IClientConnectionSink> GetForPrincipal(string principalId) {
        ArgumentException.ThrowIfNullOrEmpty(principalId);
        // O(connections) by design: the index is node-local and right-sized for the 1–10-node tier; a
        // principal index would only earn its complexity at a connection count where the seam gets
        // replaced anyway.
        return [.. _connections.Values.Where(c => string.Equals(c.Connection.PrincipalId, principalId, StringComparison.Ordinal))];
    }

    public IReadOnlyCollection<IClientConnectionSink> Connections => [.. _connections.Values];
}
