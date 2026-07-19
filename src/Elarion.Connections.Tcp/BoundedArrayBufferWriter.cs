using System.Buffers;

namespace Elarion.Connections.Tcp;

/// <summary>
/// A pooled array-backed writer that enforces a per-frame budget while allowing several frames to be
/// coalesced into one buffer: <see cref="BeginFrame"/> marks a frame boundary, the budget applies to the
/// bytes written since that mark, and <see cref="RewindFrame"/> drops a failed frame without touching the
/// ones already coalesced. Backing arrays come from <see cref="ArrayPool{T}.Shared"/>; <see cref="Trim"/>
/// returns any array grown past the initial rent, so a single large frame or batch does not pin its
/// footprint for the connection's lifetime.
/// </summary>
internal sealed class BoundedArrayBufferWriter : IBufferWriter<byte>, IDisposable {
    private readonly int _initialCapacity;
    private readonly int _maxFrameCapacity;
    private readonly int _maxTotalCapacity;
    private readonly int _retainCapacity;
    private byte[] _buffer;
    private int _written;
    private int _frameStart;

    public BoundedArrayBufferWriter(int initialCapacity, int maxFrameCapacity, int? maxTotalCapacity = null) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialCapacity, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxFrameCapacity, 0);
        if (initialCapacity > maxFrameCapacity) throw new ArgumentOutOfRangeException(nameof(initialCapacity));

        _initialCapacity = initialCapacity;
        _maxFrameCapacity = maxFrameCapacity;
        _maxTotalCapacity = Math.Max(maxFrameCapacity, maxTotalCapacity ?? maxFrameCapacity);
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        // Rent rounds up to a pool bucket; whatever it handed out for the initial size is the footprint
        // the connection keeps — only growth beyond it is trimmed back after the buffer is written out.
        _retainCapacity = _buffer.Length;
    }

    public int WrittenCount => _written;
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    /// <summary>
    /// A writable view of already-written bytes, for the in-place framing seam (ADR-0066): the framer's
    /// <c>CompleteMessage</c> backfills the reserved prologue and validates the payload through views taken
    /// here. Valid only until the next write to this writer (a write may re-rent the backing array).
    /// </summary>
    public Span<byte> GetWrittenSpan(int start, int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _written - start);
        return _buffer.AsSpan(start, length);
    }

    /// <summary>Marks the start of the next frame — the per-frame budget applies from here.</summary>
    public void BeginFrame() {
        _frameStart = _written;
    }

    /// <summary>Drops the current frame's bytes (a failed/oversized frame) without disturbing the frames
    /// already coalesced before it.</summary>
    public void RewindFrame() {
        _written = _frameStart;
    }

    public void Advance(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length - _written)
            throw new InvalidOperationException("The TCP framer advanced beyond the memory supplied by the writer.");

        _written += count;
        // A rented array may be larger than the frame budget — the budget is on the current frame's
        // written bytes, not on the pool bucket the bytes happen to sit in.
        if (_written - _frameStart > _maxFrameCapacity) throw new TcpOutboundFrameTooLargeException();
    }

    public Memory<byte> GetMemory(int sizeHint = 0) {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0) {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void ResetWrittenCount() {
        _written = 0;
        _frameStart = 0;
    }

    /// <summary>Resets and returns any array grown past the initial rent — the connection never retains
    /// the largest frame or batch it ever sent.</summary>
    public void Trim() {
        _written = 0;
        _frameStart = 0;
        if (_buffer.Length <= _retainCapacity) return;

        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = ArrayPool<byte>.Shared.Rent(_initialCapacity);
    }

    public void Dispose() {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;
        _frameStart = 0;
        if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
    }

    private void EnsureCapacity(int sizeHint) {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var requiredHint = Math.Max(sizeHint, 1);
        if (requiredHint > _maxFrameCapacity - (_written - _frameStart)) throw new TcpOutboundFrameTooLargeException();

        var required = _written + requiredHint;
        if (required <= _buffer.Length) return;

        var doubled = Math.Min((long)_maxTotalCapacity, (long)_buffer.Length * 2);
        var replacement = ArrayPool<byte>.Shared.Rent((int)Math.Max(required, doubled));
        _buffer.AsSpan(0, _written).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }
}
