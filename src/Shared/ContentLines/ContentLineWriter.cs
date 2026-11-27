using System.Buffers;
using System.Text;

namespace Elarion.ContentLines;

/// <summary>
/// Writes content lines as UTF-8 without a byte-order mark, with CRLF line ends, folded so no line is longer than 75
/// octets, never inside a UTF-8 sequence and never between a backslash and the character it escapes, each continuation
/// starting with one space (spec 0004 FR-044, spec 0005 FR-053); or unfolded on request. Lines collect in a pooled
/// buffer that <see cref="FlushAsync"/> or <see cref="Flush"/> writes to the stream.
/// </summary>
internal sealed class ContentLineWriter : IDisposable {
    /// <summary>The most octets of one physical line, without its CRLF.</summary>
    public const int MaxOctets = 75;

    private static readonly UTF8Encoding Utf8 = new(false, false);
    private readonly Stream _destination;
    private readonly StringBuilder _line = new();
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
    private byte[] _scratch = ArrayPool<byte>.Shared.Rent(1024);
    private readonly bool _fold;
    private int _length;

    /// <summary>Creates a writer over <paramref name="destination"/>.</summary>
    /// <param name="destination">The stream written to.</param>
    /// <param name="fold">Whether lines are folded at 75 octets; an unfolded line is written whole.</param>
    public ContentLineWriter(Stream destination, bool fold = true) {
        _destination = destination;
        _fold = fold;
    }

    /// <summary>The bytes waiting for <see cref="FlushAsync"/>.</summary>
    public int BufferedBytes => _length;

    /// <summary>
    /// Writes one property: <c>group.NAME;PARAM=v1,v2:value</c>. <paramref name="value"/> is written as given, so a
    /// text value must be escaped by the caller; parameter values are quoted and caret-encoded here.
    /// </summary>
    public void WriteProperty(string? group, string name, IEnumerable<(string Name, IReadOnlyList<string> Values)>? parameters, string value) {
        _line.Clear();
        if (!string.IsNullOrEmpty(group)) _line.Append(group).Append('.');
        _line.Append(name);
        if (parameters is not null) {
            foreach (var (parameterName, values) in parameters) {
                _line.Append(';').Append(parameterName).Append('=');
                for (var i = 0; i < values.Count; i++) {
                    if (i > 0) _line.Append(',');
                    _line.Append(ContentText.FormatParameterValue(values[i]));
                }
            }
        }

        _line.Append(':').Append(value);
        WriteLine(_line);
    }

    /// <summary>Writes one logical line, folding it.</summary>
    public void WriteLine(string line) {
        _line.Clear();
        _line.Append(line);
        WriteLine(_line);
    }

    /// <summary>Writes the buffered bytes to the stream.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken) {
        if (_length == 0) return;
        await _destination.WriteAsync(_buffer.AsMemory(0, _length), cancellationToken).ConfigureAwait(false);
        _length = 0;
    }

    /// <summary>Writes the buffered bytes to the stream synchronously.</summary>
    public void Flush() {
        if (_length == 0) return;
        _destination.Write(_buffer, 0, _length);
        _length = 0;
    }

    public void Dispose() {
        var buffer = Interlocked.Exchange(ref _buffer, []);
        if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
        var scratch = Interlocked.Exchange(ref _scratch, []);
        if (scratch.Length > 0) ArrayPool<byte>.Shared.Return(scratch);
    }

    private void WriteLine(StringBuilder line) {
        var byteCount = 0;
        foreach (var chunk in line.GetChunks()) byteCount += Utf8.GetByteCount(chunk.Span);
        if (_scratch.Length < byteCount) {
            ArrayPool<byte>.Shared.Return(_scratch);
            _scratch = ArrayPool<byte>.Shared.Rent(byteCount);
        }

        var written = 0;
        foreach (var chunk in line.GetChunks()) written += Utf8.GetBytes(chunk.Span, _scratch.AsSpan(written));
        var bytes = _scratch.AsSpan(0, written);

        // Folding adds three octets (CRLF and a space) per 74 octets of content at most.
        Ensure(bytes.Length + bytes.Length / 74 * 3 + 8);
        var first = true;
        while (true) {
            var room = first ? MaxOctets : MaxOctets - 1;
            if (!first) _buffer[_length++] = (byte)' ';
            if (bytes.Length <= room || !_fold) {
                bytes.CopyTo(_buffer.AsSpan(_length));
                _length += bytes.Length;
                break;
            }

            // Never inside a UTF-8 sequence, and not before a space or a tab: a vCard reader removes every leading
            // whitespace of a continuation line, so a continuation starting with one would lose it.
            var cut = room;
            while (cut > 0 && ((bytes[cut] & 0xC0) == 0x80 || bytes[cut] is (byte)' ' or (byte)'\t')) cut--;
            if (cut == 0) {
                cut = room;
                while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
            }

            // Not right after an odd run of backslashes: unfolding comes before unescaping, but a reader that unescapes
            // each physical line would split the pair. One byte earlier the run is even, and the cut is before a backslash.
            if (cut > 1 && EndsWithOddBackslashRun(bytes[..cut])) cut--;
            bytes[..cut].CopyTo(_buffer.AsSpan(_length));
            _length += cut;
            _buffer[_length++] = (byte)'\r';
            _buffer[_length++] = (byte)'\n';
            bytes = bytes[cut..];
            first = false;
        }

        _buffer[_length++] = (byte)'\r';
        _buffer[_length++] = (byte)'\n';
    }

    private static bool EndsWithOddBackslashRun(ReadOnlySpan<byte> bytes) {
        var run = 0;
        for (var i = bytes.Length - 1; i >= 0 && bytes[i] == (byte)'\\'; i--) run++;
        return run % 2 == 1;
    }

    private void Ensure(int extra) {
        if (_length + extra <= _buffer.Length) return;
        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + extra));
        _buffer.AsSpan(0, _length).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }
}
