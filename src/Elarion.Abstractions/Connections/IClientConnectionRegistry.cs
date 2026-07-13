using System.Diagnostics.CodeAnalysis;

namespace Elarion.Abstractions.Connections;

/// <summary>
/// The <b>node-local</b> index of live client connections this instance holds: adapters register a
/// connection when its handshake completes and unregister it on disconnect; consumers look connections up
/// by id or user. It is deliberately <i>not</i> a cluster directory — wanting "every connection on every
/// node" is the Orleans / managed-realtime-service trigger; authoritative cross-node addressing composes
/// from single-homed actors and the role-holder proxy instead.
/// </summary>
/// <remarks>
/// <para>
/// Registration is the lifecycle broker, which is why it is asynchronous: the default implementation
/// notifies every registered <see cref="IClientConnectionObserver"/> after the index mutation — a connect
/// observer already sees the connection in lookups, a disconnect observer no longer does — and isolates
/// observer failures so one faulty observer cannot tear down the connection or starve its peers. Adapters
/// call the registry once per lifecycle edge and get observer dispatch for free.
/// </para>
/// <para>
/// <see cref="UnregisterAsync"/> is idempotent — transports can report the same disconnect twice (abort
/// racing graceful close), and the second report is a no-op. Enumerations return point-in-time snapshots;
/// a connection may disconnect between snapshot and use, so every send stays at-most-once regardless.
/// </para>
/// </remarks>
public interface IClientConnectionRegistry {
    /// <summary>
    /// Adds <paramref name="connection"/> to the index and notifies observers. Registering an id that is
    /// already present throws — connection ids are unique per node by contract, so a collision is an
    /// adapter bug, never a race to tolerate.
    /// </summary>
    /// <param name="connection">The adapter's sink for the newly established connection.</param>
    /// <param name="ct">A cancellation token observed while notifying observers.</param>
    ValueTask RegisterAsync(IClientConnectionSink connection, CancellationToken ct = default);

    /// <summary>
    /// Removes the connection with <paramref name="connectionId"/> and notifies observers; a no-op when the
    /// id is unknown (idempotent — see remarks).
    /// </summary>
    /// <param name="connectionId">The id reported by the disconnecting transport.</param>
    /// <param name="ct">A cancellation token observed while notifying observers.</param>
    ValueTask UnregisterAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Looks up a live connection on this node by id.</summary>
    /// <param name="connectionId">The connection id to resolve.</param>
    /// <param name="connection">The sink, when the connection is currently registered here.</param>
    /// <returns><see langword="true"/> when this node holds the connection.</returns>
    bool TryGet(string connectionId, [NotNullWhen(true)] out IClientConnectionSink? connection);

    /// <summary>
    /// All live connections on this node whose <see cref="ClientConnection.PrincipalId"/> equals
    /// <paramref name="principalId"/> — one principal is many connections (a user's browser tabs; a
    /// device's parallel channels). Snapshot semantics.
    /// </summary>
    /// <param name="principalId">The stable principal identifier captured at connect.</param>
    IReadOnlyList<IClientConnectionSink> GetForPrincipal(string principalId);

    /// <summary>A point-in-time snapshot of every live connection on this node.</summary>
    IReadOnlyCollection<IClientConnectionSink> Connections { get; }
}
