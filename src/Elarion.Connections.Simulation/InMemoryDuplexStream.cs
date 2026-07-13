using System.Threading.Channels;

namespace Elarion.Connections.Simulation;

/// <summary>
/// One end of an in-memory duplex byte stream — a socket pair without the socket: what one end writes, the
/// other reads, and disposing either end reads as end-of-stream on both (writes to a closed pair fault with
/// <see cref="IOException"/>, like a closed socket). No OS handles, no ports, fully deterministic — the
/// substrate for socket-free simulation.
/// </summary>
public sealed class InMemoryDuplexStream : Stream {
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;
    private byte[]? _current;
    private int _currentOffset;
    private int _disposed;

    private InMemoryDuplexStream(Channel<byte[]> incoming, Channel<byte[]> outgoing) {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    /// <summary>Creates a connected pair; hand one end to each party.</summary>
    public static (InMemoryDuplexStream Left, InMemoryDuplexStream Right) CreatePair() {
        var leftToRight = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        var rightToLeft = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        return (new InMemoryDuplexStream(rightToLeft, leftToRight), new InMemoryDuplexStream(leftToRight, rightToLeft));
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
        if (_current is null) {
            if (!await _incoming.Reader.WaitToReadAsync(ct)) {
                return 0;
            }

            if (!_incoming.Reader.TryRead(out _current)) {
                return await ReadAsync(buffer, ct);
            }

            _currentOffset = 0;
        }

        var available = _current.Length - _currentOffset;
        var copied = Math.Min(available, buffer.Length);
        _current.AsMemory(_currentOffset, copied).CopyTo(buffer);
        _currentOffset += copied;
        if (_currentOffset == _current.Length) {
            _current = null;
        }

        return copied;
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) {
        // Copy: senders reuse their buffers (the adapter's reused send buffer included).
        if (!_outgoing.Writer.TryWrite(buffer.ToArray())) {
            throw new IOException("The in-memory duplex stream is closed.");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override void Flush() {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing) {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) {
            // Both directions end: the peer reads EOF, our own pending reads complete with EOF too.
            _outgoing.Writer.TryComplete();
            _incoming.Writer.TryComplete();
        }

        base.Dispose(disposing);
    }
}
