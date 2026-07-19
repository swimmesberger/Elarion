using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Elarion.Connections.AspNetCore;

/// <summary>
/// What an authenticator sees during the handshake: the HTTP request that carried the upgrade (cookies,
/// query, headers — for token-style auth) plus raw text-frame IO on the accepted socket (for in-socket
/// challenge/response — the device-gateway HMAC shape). No connection exists yet; nothing is registered
/// until the authenticator returns a ticket.
/// </summary>
public sealed class WebSocketHandshakeContext {
    private readonly WebSocket _socket;
    private readonly WebSocketMessageReader _reader;

    internal WebSocketHandshakeContext(HttpContext httpContext, WebSocket socket, WebSocketMessageReader reader) {
        HttpContext = httpContext;
        _socket = socket;
        _reader = reader;
    }

    /// <summary>The upgrade request — authenticate from it directly when the credential rides HTTP.</summary>
    public HttpContext HttpContext { get; }

    /// <summary>Sends one text frame to the client (e.g. the challenge nonce).</summary>
    public async ValueTask SendTextAsync(string message, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(message);
        await _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct);
    }

    /// <summary>
    /// Receives the next text frame; <see langword="null"/> when the client closed instead of answering
    /// (treat as a rejected handshake). A binary frame during the handshake is a protocol violation and throws.
    /// </summary>
    public async ValueTask<string?> ReceiveTextAsync(CancellationToken ct = default) {
        var message = await _reader.ReadAsync(ct);
        if (message is null) return null;

        if (message.Value.Type != WebSocketMessageType.Text)
            throw new InvalidOperationException("The handshake expects text frames; received a binary frame.");

        return Encoding.UTF8.GetString(message.Value.Payload.Span);
    }
}
