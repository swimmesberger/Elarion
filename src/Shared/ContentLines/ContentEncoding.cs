using System.Collections.Concurrent;
using System.Text;

namespace Elarion.ContentLines;

/// <summary>
/// The code pages a read may need, created from <see cref="CodePagesEncodingProvider.Instance"/> and held here: the
/// provider is never registered process-wide (spec 0004 FR-005, spec 0005 FR-016).
/// </summary>
internal static class CodePages {
    private static readonly ConcurrentDictionary<string, Encoding?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds the encoding named <paramref name="name"/>, built in or from the code-page provider.</summary>
    public static bool TryGet(string name, out Encoding encoding) {
        var found = Cache.GetOrAdd(name.Trim().Trim('"'), static key => Resolve(key));
        encoding = found!;
        return found is not null;
    }

    /// <summary>The encoding named <paramref name="name"/>; an unknown name is a misconfiguration.</summary>
    public static Encoding Get(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return TryGet(name, out var encoding)
            ? encoding
            : throw new InvalidOperationException($"The encoding '{name}' is not known to the runtime or its code-page provider.");
    }

    private static Encoding? Resolve(string name) {
        if (name.Length == 0) return null;
        if (name.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || name.Equals("utf8", StringComparison.OrdinalIgnoreCase))
            return new UTF8Encoding(false);

        var fromProvider = CodePagesEncodingProvider.Instance.GetEncoding(name);
        if (fromProvider is not null) return fromProvider;
        try {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException) {
            return null;
        }
    }
}

/// <summary>What a byte-order mark, or a BOM-less UTF-16 <c>BEGIN</c>, decided for the whole file.</summary>
internal readonly record struct FileEncoding(string? ByteOrderMarkEncoding, bool Transcoded, int Skip);

/// <summary>
/// The decoding ladder (ADR-0109, ADR-0110): the file's encoding from a byte-order mark or a UTF-16 sniff; otherwise
/// each logical line by its <c>CHARSET</c> where the grammar has one, then strict UTF-8, then the fallback encoding.
/// </summary>
internal sealed class ContentTextDecoder(Encoding fallback, bool fileTranscoded, bool useCharset) {
    private static readonly UTF8Encoding Utf8 = new(false, false);

    /// <summary>Whether the fallback encoding decoded any line.</summary>
    public bool UsedFallbackEncoding { get; private set; }

    /// <summary>
    /// Detects the file's encoding. A UTF-16 file is transcoded to UTF-8 into <paramref name="transcoded"/>, so the
    /// rest of the layer always reads UTF-8 or single lines in a legacy encoding.
    /// </summary>
    public static FileEncoding Detect(ReadOnlySpan<byte> data, out byte[]? transcoded) {
        transcoded = null;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return new FileEncoding("utf-8", false, 3);
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) {
            transcoded = Transcode(data[2..], Encoding.Unicode);
            return new FileEncoding("utf-16le", true, 0);
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) {
            transcoded = Transcode(data[2..], Encoding.BigEndianUnicode);
            return new FileEncoding("utf-16be", true, 0);
        }

        // Without a mark, "BEGIN" encoded as UTF-16 before the first line break.
        var firstLine = data[..Math.Min(data.Length, 512)];
        var lineEnd = firstLine.IndexOfAny((byte)'\n', (byte)'\r');
        if (lineEnd >= 0 && lineEnd + 1 < firstLine.Length && firstLine[lineEnd + 1] == 0) lineEnd++;
        if (lineEnd >= 0) firstLine = firstLine[..lineEnd];
        if (firstLine.IndexOf("B\0E\0G\0"u8) >= 0) {
            transcoded = Transcode(data, Encoding.Unicode);
            return new FileEncoding(null, true, 0);
        }

        if (firstLine.IndexOf("\0B\0E\0G"u8) >= 0) {
            transcoded = Transcode(data, Encoding.BigEndianUnicode);
            return new FileEncoding(null, true, 0);
        }

        return new FileEncoding(null, false, 0);
    }

    /// <summary>
    /// Decodes the bytes of one value or parameter. <paramref name="charset"/> is honoured only when the rule set
    /// has a <c>CHARSET</c> step and the file was not transcoded; <paramref name="unknownCharset"/> says when it named
    /// an encoding the runtime does not know.
    /// </summary>
    public string Decode(ReadOnlySpan<byte> bytes, string? charset, out bool unknownCharset) {
        unknownCharset = false;
        if (bytes.IsEmpty) return "";
        if (charset is not null && useCharset && !fileTranscoded) {
            if (CodePages.TryGet(charset, out var named)) return named.GetString(bytes);
            unknownCharset = true;
        }

        if (fileTranscoded || System.Text.Unicode.Utf8.IsValid(bytes)) return Utf8.GetString(bytes);
        UsedFallbackEncoding = true;
        return fallback.GetString(bytes);
    }

    /// <summary>Decodes bytes without a <c>CHARSET</c>.</summary>
    public string Decode(ReadOnlySpan<byte> bytes) => Decode(bytes, null, out _);

    private static byte[] Transcode(ReadOnlySpan<byte> data, Encoding encoding) {
        var text = encoding.GetString(data[..(data.Length & ~1)]);
        return Utf8.GetBytes(text);
    }
}
