using System.Buffers;

namespace Elarion.ContentLines;

/// <summary>
/// Turns bytes into logical content lines: CRLF, LF, CR and <c>\r\r\n</c> end a physical line; a following line that
/// starts with a space or a tab continues it (spec 0004 FR-006, spec 0005 FR-006). Lines are joined as bytes before
/// anything is decoded, so a fold inside a multi-byte character does not damage it. One growing pooled buffer holds
/// the current logical line, so reading is linear in the input; <see cref="Current"/> is valid until the next read.
/// </summary>
internal sealed class ContentLineReader : IDisposable {
    private readonly ReadOnlyMemory<byte> _data;
    private readonly ContentLineRules _rules;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(256);
    private int _length;
    private int _position;
    private int _physicalLine;

    // A physical line read ahead to decide whether it continues the current logical line.
    private bool _hasPeek;
    private int _peekStart;
    private int _peekLength;
    private int _peekLine;

    public ContentLineReader(ReadOnlyMemory<byte> data, ContentLineRules rules) {
        _data = data;
        _rules = rules;
    }

    /// <summary>The current logical line, unfolded; only its start when it was too long.</summary>
    public ReadOnlySpan<byte> Current => _buffer.AsSpan(0, _length);

    /// <summary>The 1-based physical line number of the current logical line's first line.</summary>
    public int LineNumber { get; private set; }

    /// <summary>Whether the current logical line exceeded its limit; its bytes past the limit were not kept.</summary>
    public bool IsTooLong { get; private set; }

    /// <summary>The limit the current logical line was held to.</summary>
    public int Limit { get; private set; }

    /// <summary>Reads the next non-blank logical line.</summary>
    public bool Read() {
        while (true) {
            if (!NextPhysical(out var start, out var length, out var lineNumber)) return false;
            var line = _data.Span.Slice(start, length);
            if (IsBlank(line)) continue;
            if (_rules.SkipCommentLines && line[0] == (byte)'#') continue;

            LineNumber = lineNumber;
            IsTooLong = false;
            _length = 0;
            Limit = _rules.LineLimitFor?.Invoke(line) ?? _rules.MaxLineBytes;
            Append(line);

            var header = Header(line);
            var quotedPrintable = _rules.QuotedPrintableSoftBreaks && ContainsIgnoreCase(header, "QUOTED-PRINTABLE"u8);
            var base64 = _rules.Base64UntilBlankLine && IsBase64Header(header);
            ContinueLine(quotedPrintable, base64, start, length);
            return true;
        }
    }

    public void Dispose() {
        var buffer = Interlocked.Exchange(ref _buffer, []);
        if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
    }

    // The soft break is decided on the physical line last appended, not on the logical line: a line already over its
    // limit no longer holds its end, and its quoted-printable continuation must still be consumed, not read as lines.
    private void ContinueLine(bool quotedPrintable, bool base64, int lastStart, int lastLength) {
        while (true) {
            if (quotedPrintable && EndsWithSoftBreak(_data.Span.Slice(lastStart, lastLength))) {
                if (!PeekPhysical(out var softStart, out var softLength)) return;
                if (!IsTooLong) _length = Current.TrimEnd(" \t"u8).Length - 1;
                ConsumePeek();
                Append(_data.Span.Slice(softStart, softLength).TrimStart(" \t"u8));
                (lastStart, lastLength) = (softStart, softLength);
                continue;
            }

            if (!PeekPhysical(out var start, out var length)) return;
            var line = _data.Span.Slice(start, length);
            if (line.Length > 0 && line[0] is (byte)' ' or (byte)'\t') {
                ConsumePeek();
                Append(_rules.UnfoldAllWhitespace ? line.TrimStart(" \t"u8) : line[1..]);
                (lastStart, lastLength) = (start, length);
                continue;
            }

            if (base64) {
                if (IsBlank(line)) {
                    ConsumePeek();
                    return;
                }

                if (IsBase64Text(line)) {
                    ConsumePeek();
                    Append(line.Trim(" \t"u8));
                    (lastStart, lastLength) = (start, length);
                    continue;
                }
            }

            return;
        }
    }

    private static bool EndsWithSoftBreak(ReadOnlySpan<byte> physicalLine) {
        var span = physicalLine.TrimEnd(" \t"u8);
        return span.Length > 0 && span[^1] == (byte)'=';
    }

    private void Append(ReadOnlySpan<byte> segment) {
        if (IsTooLong) return;
        if ((long)_length + segment.Length > Limit) {
            IsTooLong = true;
            // Keep the start of the line, so the error can still name the property.
            if (_length < 256) segment = segment[..Math.Min(segment.Length, 256 - _length)];
            else return;
        }

        if (_length + segment.Length > _buffer.Length) {
            var grown = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + segment.Length));
            _buffer.AsSpan(0, _length).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
        }

        segment.CopyTo(_buffer.AsSpan(_length));
        _length += segment.Length;
    }

    private bool NextPhysical(out int start, out int length, out int lineNumber) {
        if (PeekPhysical(out start, out length)) {
            lineNumber = _peekLine;
            ConsumePeek();
            return true;
        }

        lineNumber = 0;
        return false;
    }

    private bool PeekPhysical(out int start, out int length) {
        if (!_hasPeek) {
            if (_position >= _data.Length) {
                start = length = 0;
                return false;
            }

            var span = _data.Span;
            _peekStart = _position;
            var rest = span[_position..];
            var end = rest.IndexOfAny((byte)'\r', (byte)'\n');
            if (end < 0) {
                _peekLength = rest.Length;
                _position = span.Length;
            }
            else {
                _peekLength = end;
                var next = _position + end;
                if (span[next] == (byte)'\r') {
                    if (next + 1 < span.Length && span[next + 1] == (byte)'\n') next += 2;
                    else if (next + 2 < span.Length && span[next + 1] == (byte)'\r' && span[next + 2] == (byte)'\n') next += 3;
                    else next += 1;
                }
                else {
                    next += 1;
                }

                _position = next;
            }

            _peekLine = ++_physicalLine;
            _hasPeek = true;
        }

        start = _peekStart;
        length = _peekLength;
        return true;
    }

    private void ConsumePeek() => _hasPeek = false;

    private static bool IsBlank(ReadOnlySpan<byte> line) => line.Trim(" \t"u8).IsEmpty;

    // The name and parameters of a line: everything before the first colon outside quotes.
    private static ReadOnlySpan<byte> Header(ReadOnlySpan<byte> line) {
        var quoted = false;
        for (var i = 0; i < line.Length; i++) {
            if (line[i] == (byte)'"') quoted = !quoted;
            else if (line[i] == (byte)':' && !quoted) return line[..i];
        }

        return line;
    }

    private static bool IsBase64Header(ReadOnlySpan<byte> header) {
        if (ContainsIgnoreCase(header, "BASE64"u8)) return true;
        for (var i = 0; i + 10 <= header.Length; i++) {
            if (!header.Slice(i, 10).SequenceEqual("ENCODING=B"u8) && !EqualsIgnoreCase(header.Slice(i, 10), "ENCODING=B"u8)) continue;
            if (i + 10 == header.Length || header[i + 10] is (byte)';' or (byte)',' or (byte)' ') return true;
        }

        return false;
    }

    private static bool IsBase64Text(ReadOnlySpan<byte> line) {
        foreach (var b in line) {
            if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
                or (byte)'+' or (byte)'/' or (byte)'=' or (byte)' ' or (byte)'\t') continue;
            return false;
        }

        return true;
    }

    internal static bool ContainsIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> upperNeedle) {
        for (var i = 0; i + upperNeedle.Length <= haystack.Length; i++)
            if (EqualsIgnoreCase(haystack.Slice(i, upperNeedle.Length), upperNeedle)) return true;

        return false;
    }

    private static bool EqualsIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> upper) {
        for (var i = 0; i < value.Length; i++) {
            var b = value[i];
            if (b is >= (byte)'a' and <= (byte)'z') b -= 32;
            if (b != upper[i]) return false;
        }

        return true;
    }
}
