using System.Buffers;
using System.Globalization;
using System.Text;

namespace Elarion.Spreadsheets.Writing;

/// <summary>
/// A pooled UTF-8 buffer the writer renders XML into and flushes to a zip entry with <c>WriteAsync</c>, so a worksheet
/// never exists as a string or an XML DOM (FR-023).
/// </summary>
internal sealed class Utf8XmlBuffer : IDisposable {
    /// <summary>The size at which a worksheet writer flushes the buffer between rows.</summary>
    public const int FlushThreshold = 64 * 1024;

    // Characters element text cannot carry as they are: markup, every control character XML 1.0 forbids, the carriage
    // return (an XML parser normalizes it to a line feed), surrogates (valid pairs pass, lone ones are escaped), the two
    // non-characters, and the underscore that may start a literal _xHHHH_ sequence.
    private static readonly SearchValues<char> SpecialTextChars = SearchValues.Create(BuildSpecialTextChars());
    private static readonly SearchValues<char> SpecialAttributeChars = SearchValues.Create("&<>\"");

    private byte[] _buffer;
    private int _length;

    public Utf8XmlBuffer(int initialCapacity = FlushThreshold + 16 * 1024) {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    public int Length => _length;

    public void Append(ReadOnlySpan<byte> utf8) {
        Ensure(utf8.Length);
        utf8.CopyTo(_buffer.AsSpan(_length));
        _length += utf8.Length;
    }

    public void Append(byte value) {
        Ensure(1);
        _buffer[_length++] = value;
    }

    public void AppendFormatted<TValue>(TValue value, string? format = null) where TValue : IUtf8SpanFormattable {
        Ensure(64);
        if (!value.TryFormat(_buffer.AsSpan(_length), out var written, format, CultureInfo.InvariantCulture)) {
            Ensure(1024);
            value.TryFormat(_buffer.AsSpan(_length), out written, format, CultureInfo.InvariantCulture);
        }

        _length += written;
    }

    /// <summary>Appends UTF-8 that is known to need no escaping (ASCII markup built by the writer).</summary>
    public void AppendRaw(ReadOnlySpan<char> text) {
        Ensure(Encoding.UTF8.GetMaxByteCount(text.Length));
        _length += Encoding.UTF8.GetBytes(text, _buffer.AsSpan(_length));
    }

    /// <summary>
    /// Appends element text. Characters XML cannot carry, a carriage return and a literal <c>_xHHHH_</c> sequence use
    /// the <c>_xHHHH_</c> escape of the spreadsheet format, so the text reads back unchanged (FR-016).
    /// </summary>
    public void AppendText(ReadOnlySpan<char> text) {
        var index = 0;
        while (index < text.Length) {
            var relative = text[index..].IndexOfAny(SpecialTextChars);
            var end = relative < 0 ? text.Length : index + relative;
            if (end > index) AppendRaw(text[index..end]);
            if (relative < 0) return;

            var c = text[end];
            switch (c) {
                case '&':
                    Append("&amp;"u8);
                    break;
                case '<':
                    Append("&lt;"u8);
                    break;
                case '>':
                    Append("&gt;"u8);
                    break;
                case '_':
                    if (StartsEscapeSequence(text, end)) Append("_x005F_"u8);
                    else Append((byte)'_');
                    break;
                default:
                    if (char.IsHighSurrogate(c) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1])) {
                        AppendRaw(text.Slice(end, 2));
                        index = end + 2;
                        continue;
                    }

                    AppendHexEscape(c);
                    break;
            }

            index = end + 1;
        }
    }

    /// <summary>Appends an attribute value; the caller has refused characters XML cannot carry.</summary>
    public void AppendAttribute(ReadOnlySpan<char> text) {
        var index = 0;
        while (index < text.Length) {
            var relative = text[index..].IndexOfAny(SpecialAttributeChars);
            var end = relative < 0 ? text.Length : index + relative;
            if (end > index) AppendRaw(text[index..end]);
            if (relative < 0) return;

            Append(text[end] switch {
                '&' => "&amp;"u8,
                '<' => "&lt;"u8,
                '>' => "&gt;"u8,
                _ => "&quot;"u8
            });
            index = end + 1;
        }
    }

    public async ValueTask FlushAsync(Stream destination, CancellationToken cancellationToken) {
        if (_length == 0) return;

        await destination.WriteAsync(_buffer.AsMemory(0, _length), cancellationToken).ConfigureAwait(false);
        _length = 0;
    }

    public void Dispose() {
        var buffer = _buffer;
        _buffer = [];
        _length = 0;
        if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
    }

    /// <summary>Whether XML 1.0 can carry <paramref name="text"/> in an attribute.</summary>
    public static bool IsXmlCharacterData(ReadOnlySpan<char> text) {
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                i++;
                continue;
            }

            if (c is '\t' or '\n' or '\r') continue;
            if (c < 0x20 || char.IsSurrogate(c) || c is '￾' or '￿') return false;
        }

        return true;
    }

    private void AppendHexEscape(char c) {
        Ensure(7);
        var span = _buffer.AsSpan(_length);
        span[0] = (byte)'_';
        span[1] = (byte)'x';
        ((ushort)c).TryFormat(span[2..6], out _, "X4", CultureInfo.InvariantCulture);
        span[6] = (byte)'_';
        _length += 7;
    }

    private static bool StartsEscapeSequence(ReadOnlySpan<char> text, int index) {
        return index + 6 < text.Length
               && text[index + 1] == 'x'
               && char.IsAsciiHexDigit(text[index + 2])
               && char.IsAsciiHexDigit(text[index + 3])
               && char.IsAsciiHexDigit(text[index + 4])
               && char.IsAsciiHexDigit(text[index + 5])
               && text[index + 6] == '_';
    }

    private void Ensure(int additional) {
        if (_length + additional <= _buffer.Length) return;

        var larger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + additional));
        _buffer.AsSpan(0, _length).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }

    private static char[] BuildSpecialTextChars() {
        var chars = new List<char> { '&', '<', '>', '_', '\r', '￾', '￿' };
        for (var c = '\0'; c < ' '; c++)
            if (c is not ('\t' or '\n' or '\r'))
                chars.Add(c);

        for (var c = '\uD800'; c <= '\uDFFF'; c++) chars.Add(c);

        return [.. chars];
    }
}
