using System.Buffers;

namespace Elarion.ContentLines;

/// <summary>
/// The bytes of one read, held in a pooled array. Reading stops as soon as one byte more than the limit arrived, so a
/// too-large upload never occupies more than the limit (spec 0004 FR-054, spec 0005 FR-046).
/// </summary>
internal sealed class ContentBuffer : IDisposable {
    private byte[]? _array;

    private ContentBuffer(byte[] array, int length, bool tooLarge) {
        _array = array;
        Length = length;
        TooLarge = tooLarge;
    }

    /// <summary>The number of bytes read.</summary>
    public int Length { get; }

    /// <summary>Whether the input is longer than the limit; the bytes are then incomplete and must not be read.</summary>
    public bool TooLarge { get; }

    /// <summary>The bytes read.</summary>
    public ReadOnlyMemory<byte> Memory => (_array ?? throw new ObjectDisposedException(nameof(ContentBuffer))).AsMemory(0, Length);

    /// <summary>Reads <paramref name="source"/> to its end or one byte past <paramref name="maxBytes"/>.</summary>
    public static async ValueTask<ContentBuffer> ReadAsync(Stream source, long maxBytes, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(source);
        var limit = (int)Math.Min(maxBytes, Array.MaxLength - 1);
        var initial = 16 * 1024;
        if (source.CanSeek) {
            var remaining = source.Length - source.Position;
            if (remaining > limit) return new ContentBuffer(ArrayPool<byte>.Shared.Rent(1), 0, tooLarge: true);
            initial = (int)Math.Max(1, remaining + 1);
        }

        var array = ArrayPool<byte>.Shared.Rent(Math.Min(initial, limit + 1));
        var length = 0;
        try {
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                if (length == array.Length) {
                    if (length > limit) break;
                    var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)array.Length * 2, limit + 1L));
                    array.AsSpan(0, length).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(array);
                    array = grown;
                }

                var read = await source.ReadAsync(array.AsMemory(length, Math.Min(array.Length, limit + 1) - length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                if (length > limit) break;
            }
        }
        catch {
            ArrayPool<byte>.Shared.Return(array);
            throw;
        }

        return new ContentBuffer(array, length, tooLarge: length > limit);
    }

    /// <summary>Returns the array to the pool.</summary>
    public void Dispose() {
        var array = Interlocked.Exchange(ref _array, null);
        if (array is not null) ArrayPool<byte>.Shared.Return(array);
    }
}
