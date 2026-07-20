namespace Elarion.Connections.AspNetCore;

/// <summary>
/// The app's per-endpoint connection policy, resolved from DI by <c>MapElarionConnectionSocket</c>: a
/// factory that creates one <see cref="WebSocketConnectionSession"/> per upgrade request. Everything
/// else — accept mechanics, message reassembly, registry lifecycle, observer dispatch — is the adapter's.
/// </summary>
/// <remarks>
/// The handler serves every connection on its endpoint concurrently, so it holds no per-connection
/// state — that is what the session is for. Returning <see langword="null"/> rejects the upgrade request
/// with <c>403 Forbidden</c> before the socket is accepted: the place for binding-configuration denial
/// ("no device is provisioned for this route"). Register the concrete handler in DI (singleton for
/// stateless handlers; scoped works too — for a WebSocket, the request scope lives as long as the
/// connection).
/// </remarks>
public abstract class WebSocketConnectionHandler {
    /// <summary>
    /// Creates the connection's session, called with the upgrade request <b>before the socket is
    /// accepted</b> — so the session's keep-alive interval and size cap govern the handshake too. This is
    /// the binding-configuration lookup point: resolve the peer's configuration from route values, query,
    /// or headers (which may be asynchronous) and hand what the session needs to its constructor.
    /// <see langword="null"/> rejects the request.
    /// </summary>
    /// <param name="context">The upgrade request (route values, query, headers, user).</param>
    /// <param name="ct">Aborted when the client disconnects first.</param>
    public abstract ValueTask<WebSocketConnectionSession?> CreateSessionAsync(
        Microsoft.AspNetCore.Http.HttpContext context, CancellationToken ct);
}

/// <summary>
/// One connection's policy and negotiated state, created by
/// <see cref="WebSocketConnectionHandler.CreateSessionAsync"/> before the socket is accepted: it supplies
/// the connection's settings, authenticates the handshake, and creates the codec. The session lives
/// exactly as long as its connection, so per-connection state — the binding-configuration row, key
/// material derived during authentication — lives in typed session fields, with no correlation lookups
/// and no singleton-handler races.
/// </summary>
/// <remarks>
/// <see cref="AuthenticateAsync"/> runs after the socket is accepted and before the connection exists
/// anywhere: it may authenticate from the HTTP request (cookie, query token, header) or run an in-socket
/// challenge/response over <see cref="WebSocketHandshakeContext.SendTextAsync"/> /
/// <see cref="WebSocketHandshakeContext.ReceiveTextAsync"/> (the HMAC-style device handshake). Returning
/// <see langword="null"/> rejects: the adapter closes the socket with a policy-violation status and
/// nothing was ever registered.
/// </remarks>
public abstract class WebSocketConnectionSession {
    /// <summary>
    /// This connection's overrides of the endpoint options — typically assembled in the session's
    /// constructor from the binding configuration the handler resolved. <see langword="null"/> (the
    /// default) inherits the endpoint options unchanged. Read once, before the socket is accepted.
    /// </summary>
    public virtual WebSocketConnectionSettings? Settings => null;

    /// <summary>Authenticates the socket; <see langword="null"/> rejects it.</summary>
    /// <param name="handshake">The HTTP context plus pre-registration frame IO for in-socket challenges.</param>
    /// <param name="ct">Aborted when the client disconnects mid-handshake.</param>
    public abstract ValueTask<ClientConnectionTicket?> AuthenticateAsync(
        WebSocketHandshakeContext handshake, CancellationToken ct);

    /// <summary>Creates the codec for the authenticated connection (called once, before registration — the
    /// first observer greeting can already flow through it). State the codec needs — key material derived
    /// in <see cref="AuthenticateAsync"/> — flows from session fields into the codec's constructor.</summary>
    /// <param name="connection">The connection the codec belongs to (raw send legs live here).</param>
    public abstract IClientConnectionProtocol CreateProtocol(WebSocketClientConnection connection);
}
