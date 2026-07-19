namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Wraps a forward-only source stream so it presents a caller-declared length, letting the Npgsql
/// <c>bytea</c> bind stream a non-seekable source straight through the binary protocol (which needs
/// the value length up front) without buffering it first.
/// </summary>
/// <remarks>
/// The declared length is trusted only as far as the write: reads are capped at it so the protocol
/// can never be handed more bytes than promised, <see cref="BytesRead"/> exposes how many bytes were
/// actually consumed, and <see cref="HasUnreadInnerDataAsync"/> lets the caller detect a source that
/// is longer than the hint. The store uses both to fail the transaction when the actual content does
/// not match the hint, so the recorded size stays truthful. The wrapped stream is never disposed —
/// the caller owns it.
/// </remarks>
internal sealed class HintedLengthStream(Stream inner, long length) : Stream {
    private long _position;

    /// <summary>Gets the number of bytes read from the source so far.</summary>
    public long BytesRead => _position;

    public override bool CanRead => true;

    // Reported as seekable so Npgsql reads Length and streams the value rather than buffering it.
    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position {
        get => _position;
        set {
            if (value != _position) throw new NotSupportedException($"{nameof(HintedLengthStream)} is forward-only.");
        }
    }

    public override int Read(byte[] buffer, int offset, int count) {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer) {
        var allowed = (int)Math.Min(buffer.Length, length - _position);
        if (allowed <= 0) return 0;

        var read = inner.Read(buffer[..allowed]);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        var allowed = (int)Math.Min(buffer.Length, length - _position);
        if (allowed <= 0) return 0;

        var read = await inner.ReadAsync(buffer[..allowed], cancellationToken);
        _position += read;
        return read;
    }

    /// <summary>
    /// Probes the source for a byte beyond the declared length, indicating the hint understated the
    /// actual content length.
    /// </summary>
    public async ValueTask<bool> HasUnreadInnerDataAsync(CancellationToken cancellationToken) {
        var probe = new byte[1];
        return await inner.ReadAsync(probe, cancellationToken) > 0;
    }

    public override void Flush() {
    }

    public override long Seek(long offset, SeekOrigin origin) {
        throw new NotSupportedException($"{nameof(HintedLengthStream)} is forward-only.");
    }

    public override void SetLength(long value) {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) {
        throw new NotSupportedException();
    }
}
