namespace Elarion.Abstractions.Connections;

/// <summary>
/// The connection lifecycle seam (Rx-shaped, no Rx dependency — the client-events observer pattern):
/// implementations learn when a connection is established or ends, on whatever transport carried it.
/// Presence projections, device registration, and the connection-owning actor all hang off this.
/// </summary>
/// <remarks>
/// <para>
/// Register with <c>TryAddEnumerable</c>; every registered observer runs per lifecycle edge, dispatched by
/// the <see cref="IClientConnectionRegistry"/> default, which isolates failures — an observer throwing is
/// logged and must never tear down the connection or suppress its peers.
/// </para>
/// <para>
/// <see cref="OnConnectedAsync"/> receives the <b>sink</b> so a producer can greet the new arrival
/// immediately (the initial-value posture client events use: the newcomer starts converged without
/// re-broadcasting to everyone). <see cref="OnDisconnectedAsync"/> receives only the identity record —
/// the sink is dead and there is deliberately nothing left to write to.
/// </para>
/// </remarks>
public interface IClientConnectionObserver {
    /// <summary>Called after a connection completes its handshake, before regular traffic flows.</summary>
    /// <param name="connection">The new connection's sink (greeting-capable).</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default);

    /// <summary>Called after a connection ended, for any reason — graceful close, drop, or node shutdown.</summary>
    /// <param name="connection">The identity of the connection that ended.</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default);

    /// <summary>
    /// Called after an anonymous connection identity was atomically promoted. The registry has already
    /// committed <paramref name="current"/> before observers run; failures are isolated and never roll back.
    /// Existing authorization-derived state should be invalidated and rebuilt under the new identity.
    /// </summary>
    /// <param name="previous">The anonymous snapshot that was replaced.</param>
    /// <param name="current">The authenticated snapshot now exposed by the sink.</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask OnIdentityPromotedAsync(
        ClientConnection previous,
        ClientConnection current,
        CancellationToken ct = default);
}
