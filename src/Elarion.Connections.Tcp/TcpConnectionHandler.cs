namespace Elarion.Connections.Tcp;

/// <summary>
/// The app's per-endpoint TCP connection policy, resolved from DI by the listener/dialer services: a
/// factory that creates one <see cref="TcpConnectionSession"/> per accepted or dialed link. Everything
/// else — sockets, framing, registry lifecycle, reconnect (dial-out) — is the adapter's.
/// </summary>
/// <remarks>
/// The handler is a singleton serving every connection on its endpoint concurrently, so it holds no
/// per-connection state — that is what the session is for. Returning <see langword="null"/> rejects the
/// link before any byte is exchanged (the socket closes; nothing is registered): the place for
/// binding-configuration denial ("no device is provisioned for this peer").
/// </remarks>
public abstract class TcpConnectionHandler {
    /// <summary>
    /// Creates the connection's session, called with the peer's endpoints <b>before any byte is
    /// exchanged</b> — so the session's framer governs the handshake too. This is the
    /// binding-configuration lookup point: resolve the peer's configuration (which may be asynchronous)
    /// and hand what the session needs to its constructor. <see langword="null"/> rejects the link.
    /// </summary>
    /// <param name="peer">The connection's endpoints (all that exists this early).</param>
    /// <param name="ct">Fires on host shutdown.</param>
    public abstract ValueTask<TcpConnectionSession?> CreateSessionAsync(TcpConnectionPeer peer, CancellationToken ct);
}
