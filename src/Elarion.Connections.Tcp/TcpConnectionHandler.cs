namespace Elarion.Connections.Tcp;

/// <summary>
/// The app's per-endpoint TCP connection policy, resolved from DI by the listener/dialer services: it
/// authenticates the handshake and creates the connection's codec. Everything else — sockets, framing,
/// registry lifecycle, reconnect (dial-out) — is the adapter's.
/// </summary>
/// <remarks>
/// Device protocols frequently have no credential exchange at all — identity comes from the binding
/// (which port, which peer): return a ticket immediately from
/// <see cref="TcpHandshakeContext.RemoteEndPoint"/>/configuration. For challenge/response protocols,
/// exchange framed messages on the context. Returning <see langword="null"/> rejects: the socket closes
/// and nothing was ever registered. The handshake runs under the endpoint's <c>HandshakeTimeout</c>.
/// </remarks>
public abstract class TcpConnectionHandler {
    /// <summary>Authenticates a new link; <see langword="null"/> rejects it.</summary>
    /// <param name="handshake">The endpoints plus framed message IO for challenge/response exchanges.</param>
    /// <param name="ct">Fires on handshake timeout, peer disconnect, or host shutdown.</param>
    public abstract ValueTask<ClientConnectionTicket?> AuthenticateAsync(
        TcpHandshakeContext handshake, CancellationToken ct);

    /// <summary>Creates the codec for an authenticated connection (called once, before registration).</summary>
    /// <param name="connection">The connection the codec belongs to (raw framed send legs live here).</param>
    public abstract IClientConnectionProtocol CreateProtocol(TcpClientConnection connection);
}
