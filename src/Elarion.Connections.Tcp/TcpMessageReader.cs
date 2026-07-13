namespace Elarion.Connections.Tcp;

/// <summary>
/// Accumulates the byte stream and extracts complete messages through the framer, with a hard size cap —
/// shared by the handshake IO and the main receive loop so both enforce the same limit. A returned
/// message's payload slices the internal buffer and is only valid until the next read.
/// </summary>
internal sealed class TcpMessageReader(Stream stream, TcpMessageFramer framer, int maxMessageBytes, int initialBufferBytes) {
    private byte[] _buffer = new byte[initialBufferBytes];
    private int _start;
    private int _end;

    /// <summary>Reads the next complete message; <see langword="null"/> when the peer closed the stream.</summary>
    /// <exception cref="TcpMessageTooLargeException">The unconsumed buffer exceeded the cap without
    /// yielding a message.</exception>
    public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(CancellationToken ct) {
        while (true) {
            if (_end > _start) {
                var complete = framer.TryReadMessage(
                    _buffer.AsMemory(_start, _end - _start), out var consumed, out var message);
                // Consumed applies either way: framers drop skippable noise even without a complete
                // message, so noise neither accumulates against the cap nor gets rescanned per read.
                _start += consumed;
                if (complete) {
                    return message;
                }
            }

            if (_end - _start >= maxMessageBytes) {
                throw new TcpMessageTooLargeException();
            }

            if (_start > 0) {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length) {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct);
            if (read == 0) {
                return null;
            }

            _end += read;
        }
    }
}

/// <summary>Signals unconsumed bytes beyond the configured cap; the adapter closes the connection.</summary>
internal sealed class TcpMessageTooLargeException : Exception;
