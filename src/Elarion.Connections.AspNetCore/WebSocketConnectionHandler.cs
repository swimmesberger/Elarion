namespace Elarion.Connections.AspNetCore;

/// <summary>
/// The app's per-endpoint connection policy, resolved from DI by <c>MapElarionConnectionSocket</c>: it
/// authenticates the handshake and creates the connection's codec. Everything else — accept mechanics,
/// message reassembly, registry lifecycle, observer dispatch — is the adapter's.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AuthenticateAsync"/> runs after the socket is accepted and before the connection exists
/// anywhere: it may authenticate from the HTTP request (cookie, query token, header) or run an in-socket
/// challenge/response over <see cref="WebSocketHandshakeContext.SendTextAsync"/> /
/// <see cref="WebSocketHandshakeContext.ReceiveTextAsync"/> (the HMAC-style device handshake). Returning
/// <see langword="null"/> rejects: the adapter closes the socket with a policy-violation status and nothing
/// was ever registered.
/// </para>
/// <para>
/// Register the concrete handler in DI (singleton for stateless handlers; scoped works too — for a
/// WebSocket, the request scope lives as long as the connection).
/// </para>
/// </remarks>
public abstract class WebSocketConnectionHandler {
    /// <summary>Authenticates a new socket; <see langword="null"/> rejects it.</summary>
    /// <param name="handshake">The HTTP context plus pre-registration frame IO for in-socket challenges.</param>
    /// <param name="ct">Aborted when the client disconnects mid-handshake.</param>
    public abstract ValueTask<ClientConnectionTicket?> AuthenticateAsync(
        WebSocketHandshakeContext handshake, CancellationToken ct);

    /// <summary>Creates the codec for an authenticated connection (called once, before registration — the
    /// first observer greeting can already flow through it).</summary>
    /// <param name="connection">The connection the codec belongs to (raw send legs live here).</param>
    public abstract IClientConnectionProtocol CreateProtocol(WebSocketClientConnection connection);
}
