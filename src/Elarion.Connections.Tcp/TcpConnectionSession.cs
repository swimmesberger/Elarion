namespace Elarion.Connections.Tcp;

/// <summary>
/// One connection's policy and negotiated state, created by
/// <see cref="TcpConnectionHandler.CreateSessionAsync"/> before any byte is exchanged: it supplies the
/// connection's settings, authenticates the handshake, and creates the codec. The session lives exactly
/// as long as its connection, so per-connection state — the binding-configuration row, a stateful framer,
/// key material derived during authentication — lives in typed session fields, with no correlation
/// lookups, no downcasts, and no singleton-handler races.
/// </summary>
/// <remarks>
/// Device protocols frequently have no credential exchange at all — identity comes from the binding
/// (which port, which peer): return a ticket immediately from what the handler's lookup handed the
/// session. For challenge/response protocols, exchange framed messages on the handshake context; the
/// handshake runs under the endpoint's <c>HandshakeTimeout</c>. A protocol that negotiates framing state
/// (e.g. switches into encrypted framing after a key exchange) keeps its stateful framer in a session
/// field: return it via <see cref="Settings"/>, then flip it from <see cref="AuthenticateAsync"/> —
/// the handshake is sequential, so no send or read is in flight between its calls — or from the codec's
/// inbound path after awaiting the mode-switch send (a completed send means the frame was physically
/// written, so the flip lands exactly between it and the next outbound frame).
/// </remarks>
public abstract class TcpConnectionSession {
    /// <summary>
    /// This connection's overrides of the endpoint options — typically assembled in the session's
    /// constructor from the binding configuration the handler resolved. <see langword="null"/> (the
    /// default) inherits the endpoint options unchanged. A stateful framer belongs here, created per
    /// session; the shared endpoint framer stays stateless by contract. Read once, before the handshake.
    /// </summary>
    public virtual TcpConnectionSettings? Settings => null;

    /// <summary>Authenticates the link; <see langword="null"/> rejects it (the socket closes and nothing
    /// was ever registered).</summary>
    /// <param name="handshake">The endpoints plus framed message IO for challenge/response exchanges.</param>
    /// <param name="ct">Fires on handshake timeout, peer disconnect, or host shutdown.</param>
    public abstract ValueTask<ClientConnectionTicket?> AuthenticateAsync(
        TcpHandshakeContext handshake, CancellationToken ct);

    /// <summary>Creates the codec for the authenticated connection (called once, before registration).
    /// State the codec needs — the session's framer, key material derived in
    /// <see cref="AuthenticateAsync"/> — flows from session fields into the codec's constructor.</summary>
    /// <param name="connection">The connection the codec belongs to (raw framed send legs live here).</param>
    public abstract IClientConnectionProtocol CreateProtocol(TcpClientConnection connection);
}
