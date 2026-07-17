using System.Buffers;

namespace Elarion.Connections.Tcp;

/// <summary>An array-backed writer whose capacity can never exceed one configured frame budget.</summary>
internal sealed class BoundedArrayBufferWriter : IBufferWriter<byte> {
    private readonly int _maxCapacity;
    private byte[] _buffer;
    private int _written;

    public BoundedArrayBufferWriter(int initialCapacity, int maxCapacity) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialCapacity, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxCapacity, 0);
        if (initialCapacity > maxCapacity) {
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        }

        _buffer = new byte[initialCapacity];
        _maxCapacity = maxCapacity;
    }

    public int WrittenCount => _written;
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public void Advance(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length - _written) {
            throw new InvalidOperationException("The TCP framer advanced beyond the memory supplied by the writer.");
        }

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0) {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void ResetWrittenCount() => _written = 0;

    private void EnsureCapacity(int sizeHint) {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var requiredHint = Math.Max(sizeHint, 1);
        if (requiredHint > _maxCapacity - _written) {
            throw new TcpOutboundFrameTooLargeException();
        }

        var required = _written + requiredHint;
        if (required <= _buffer.Length) {
            return;
        }

        var doubled = Math.Min((long)_maxCapacity, (long)_buffer.Length * 2);
        var newLength = (int)Math.Max(required, doubled);
        Array.Resize(ref _buffer, newLength);
    }
}
