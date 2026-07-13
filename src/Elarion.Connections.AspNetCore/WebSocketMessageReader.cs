using System.Net.WebSockets;

namespace Elarion.Connections.AspNetCore;

/// <summary>
/// Reassembles complete WebSocket messages (a message may span many frames) with a hard size cap, shared by
/// the handshake IO and the main receive loop so both enforce the same limit.
/// </summary>
internal sealed class WebSocketMessageReader(WebSocket socket, int maxMessageBytes, int receiveBufferBytes) {
    private readonly byte[] _buffer = new byte[receiveBufferBytes];

    /// <summary>Reads the next complete message; <see langword="null"/> when the client sent a close frame.</summary>
    /// <exception cref="WebSocketMessageTooLargeException">The message exceeded the configured cap.</exception>
    public async ValueTask<WebSocketInboundMessage?> ReadAsync(CancellationToken ct) {
        using var assembled = new MemoryStream();
        while (true) {
            var result = await socket.ReceiveAsync(_buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) {
                return null;
            }

            assembled.Write(_buffer, 0, result.Count);
            if (assembled.Length > maxMessageBytes) {
                throw new WebSocketMessageTooLargeException();
            }

            if (result.EndOfMessage) {
                return new WebSocketInboundMessage(result.MessageType, assembled.ToArray());
            }
        }
    }
}

/// <summary>One reassembled inbound message.</summary>
internal readonly record struct WebSocketInboundMessage(WebSocketMessageType Type, byte[] Payload);

/// <summary>Signals a message over the configured cap; the endpoint closes with <c>MessageTooBig</c>.</summary>
internal sealed class WebSocketMessageTooLargeException : Exception;
