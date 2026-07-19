using System.Runtime.InteropServices;

namespace Elarion.Connections.Tcp;

/// <summary>
/// Accumulates the byte stream and extracts complete messages through the framer, with a hard cap on the
/// total wire bytes of one unconsumed frame (prefix/header/body/trailer included). The same reader serves
/// handshake IO and the main receive loop, so both enforce the same limit. A returned message's payload
/// slices the internal buffer and is only valid until the next read.
/// </summary>
internal sealed class TcpMessageReader {
    private readonly Stream _stream;
    private readonly TcpMessageFramer _framer;
    private readonly int _maxMessageBytes;
    private byte[] _buffer;
    private int _start;
    private int _end;

    public TcpMessageReader(Stream stream, TcpMessageFramer framer, int maxMessageBytes, int initialBufferBytes) {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(framer);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxMessageBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialBufferBytes, 0);
        if (initialBufferBytes > maxMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(initialBufferBytes),
                "The initial read buffer cannot exceed the maximum framed wire-message size.");

        _stream = stream;
        _framer = framer;
        _maxMessageBytes = maxMessageBytes;
        _buffer = new byte[initialBufferBytes];
    }

    internal int BufferCapacity => _buffer.Length;

    /// <summary>Reads the next complete message; <see langword="null"/> when the peer closed the stream.</summary>
    /// <exception cref="TcpMessageTooLargeException">The total unconsumed framed wire bytes exceeded the
    /// configured cap without yielding a complete message.</exception>
    /// <exception cref="TcpMessageFramingException">The configured framer returned an invalid result.</exception>
    public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(CancellationToken ct) {
        while (true) {
            var available = _end - _start;
            if (available > 0) {
                var presented = _buffer.AsMemory(_start, available);
                var complete = _framer.TryReadMessage(presented, out var consumed, out var message);
                ValidateFramerResult(presented, available, complete, consumed, message);

                // Consumed applies either way: framers drop skippable noise even without a complete
                // message, so noise neither accumulates against the cap nor gets rescanned per read.
                _start += consumed;
                if (complete) return message;
            }

            var unconsumed = _end - _start;
            if (unconsumed >= _maxMessageBytes) throw new TcpMessageTooLargeException();

            if (_start > 0) {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, unconsumed);
                _end = unconsumed;
                _start = 0;
            }

            EnsureReadCapacity(unconsumed);
            var readBudget = Math.Min(_buffer.Length - _end, _maxMessageBytes - unconsumed);
            // The precondition above guarantees a positive budget. Keeping it explicit prevents a future
            // capacity change from issuing a zero-length read (whose EOF-like result would be misleading).
            if (readBudget <= 0) throw new TcpMessageTooLargeException();

            var read = await _stream.ReadAsync(_buffer.AsMemory(_end, readBudget), ct);
            if (read == 0) return null;

            _end += read;
        }
    }

    private void EnsureReadCapacity(int unconsumed) {
        if (_end < _buffer.Length) return;

        // The endpoint validates the initial size; all later growth is capped here too. Calculate through
        // long so a pathological configured cap cannot overflow a doubling operation.
        var doubled = Math.Min((long)_maxMessageBytes, (long)_buffer.Length * 2);
        var required = (long)unconsumed + 1;
        var newLength = (int)Math.Max(required, doubled);
        Array.Resize(ref _buffer, newLength);
    }

    private static void ValidateFramerResult(
        ReadOnlyMemory<byte> presented, int available, bool complete, int consumed, ReadOnlyMemory<byte> message) {
        if (consumed < 0 || consumed > available)
            throw new TcpMessageFramingException(
                "The TCP framer returned a consumed byte count outside the presented buffer.");

        if (!complete) return;

        if (consumed == 0)
            throw new TcpMessageFramingException("The TCP framer completed a message without consuming any bytes.");

        // Memory.Equals cannot establish that one memory is a slice of another. The reader always presents
        // an array-backed slice, so require the returned memory to reference that exact array and prove its
        // offset/range lie inside both the presented data and the reported consumed region.
        if (!MemoryMarshal.TryGetArray(presented, out var presentedSegment)
            || !MemoryMarshal.TryGetArray(message, out var messageSegment)
            || !ReferenceEquals(presentedSegment.Array, messageSegment.Array)
            || messageSegment.Offset < presentedSegment.Offset
            || (long)messageSegment.Offset + messageSegment.Count > (long)presentedSegment.Offset + consumed)
            throw new TcpMessageFramingException(
                "The TCP framer returned a complete message outside the presented consumed buffer region.");
    }
}

/// <summary>Signals unconsumed framed wire bytes beyond the configured cap; the adapter closes the connection.</summary>
internal sealed class TcpMessageTooLargeException : Exception;

/// <summary>Signals that a custom TCP framer violated its declared buffer/consumption contract.</summary>
internal sealed class TcpMessageFramingException(string message) : Exception(message);
