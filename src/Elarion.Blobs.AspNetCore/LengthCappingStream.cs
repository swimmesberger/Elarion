namespace Elarion.Blobs.AspNetCore;

/// <summary>
/// Thrown when an upload exceeds the configured maximum size.
/// </summary>
public sealed class BlobUploadTooLargeException(long limit)
    : Exception($"The upload exceeded the maximum allowed size of {limit} bytes.") {
    /// <summary>Gets the configured byte limit that was exceeded.</summary>
    public long Limit { get; } = limit;
}

/// <summary>
/// A forward-only read wrapper that throws <see cref="BlobUploadTooLargeException"/> once more than a
/// byte limit has been read, so an upload of unknown length cannot exhaust memory while being buffered.
/// </summary>
internal sealed class LengthCappingStream(Stream inner, long limit) : Stream {
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) {
        var read = inner.Read(buffer, offset, count);
        Track(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Track(read);
        return read;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Track(int read) {
        _read += read;
        if (_read > limit) {
            throw new BlobUploadTooLargeException(limit);
        }
    }
}
