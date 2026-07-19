using System.Buffers;
using System.Net.WebSockets;

namespace Elarion.Connections.AspNetCore;

/// <summary>
/// Reassembles complete WebSocket messages (a message may span many frames) with a hard size cap, shared by
/// the handshake IO and the main receive loop so both enforce the same limit. Reassembly happens in one
/// pooled per-connection buffer, so a steady message stream allocates nothing per message (ADR-0066): the
/// returned payload is a slice of that buffer, valid only until the next <see cref="ReadAsync"/> — the same
/// call-scoped contract the TCP reader has always had and <c>IClientConnectionProtocol.OnBinaryAsync</c>
/// documents.
/// </summary>
internal sealed class WebSocketMessageReader : IDisposable {
    private readonly WebSocket _socket;
    private readonly int _maxMessageBytes;
    private readonly int _retainCapacity;
    private byte[] _buffer;

    public WebSocketMessageReader(WebSocket socket, int maxMessageBytes, int receiveBufferBytes) {
        _socket = socket;
        _maxMessageBytes = maxMessageBytes;
        _buffer = ArrayPool<byte>.Shared.Rent(receiveBufferBytes);
        // Rent rounds up to a pool bucket; whatever it handed out for the initial size is the footprint the
        // connection keeps — growth beyond it is trimmed back before the next read (mirrors the TCP writer's
        // retain policy) so one oversized message never pins its footprint for the connection's lifetime.
        _retainCapacity = _buffer.Length;
    }

    /// <summary>Reads the next complete message; <see langword="null"/> when the client sent a close frame.
    /// The payload slices the reader's pooled buffer and is only valid until the next call.</summary>
    /// <exception cref="WebSocketMessageTooLargeException">The message exceeded the configured cap.</exception>
    public async ValueTask<WebSocketInboundMessage?> ReadAsync(CancellationToken ct) {
        if (_buffer.Length > _retainCapacity) {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = ArrayPool<byte>.Shared.Rent(_retainCapacity);
        }

        var assembled = 0;
        while (true) {
            if (assembled == _buffer.Length)
                Grow(assembled);

            var result = await _socket.ReceiveAsync(_buffer.AsMemory(assembled), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;

            assembled += result.Count;
            if (assembled > _maxMessageBytes) throw new WebSocketMessageTooLargeException();

            if (result.EndOfMessage)
                return new WebSocketInboundMessage(result.MessageType, _buffer.AsMemory(0, assembled));
        }
    }

    public void Dispose() {
        var buffer = _buffer;
        _buffer = [];
        if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
    }

    private void Grow(int assembled) {
        // Double up to one byte past the cap — enough for the next read to prove the overflow and throw.
        var target = Math.Max(assembled + 1L, Math.Min((long)_buffer.Length * 2, _maxMessageBytes + 1L));
        var replacement = ArrayPool<byte>.Shared.Rent((int)target);
        _buffer.AsSpan(0, assembled).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }
}

/// <summary>One reassembled inbound message; the payload is call-scoped (valid until the next read).</summary>
internal readonly record struct WebSocketInboundMessage(WebSocketMessageType Type, ReadOnlyMemory<byte> Payload);

/// <summary>Signals a message over the configured cap; the endpoint closes with <c>MessageTooBig</c>.</summary>
internal sealed class WebSocketMessageTooLargeException : Exception;
