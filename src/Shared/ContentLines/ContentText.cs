using System.Text;

namespace Elarion.ContentLines;

/// <summary>
/// Escaping of text values (RFC 5545 section 3.3.11, RFC 6350 section 3.4): <c>\\</c>, <c>\;</c>, <c>\,</c>, <c>\:</c>
/// and <c>\n</c> or <c>\N</c> for a line break; any other backslash sequence reads as the character after it.
/// </summary>
internal static class ContentText {
    /// <summary>Unescapes a text value and normalizes its line breaks to <c>\n</c>.</summary>
    public static string Unescape(ReadOnlySpan<char> value) {
        if (value.IndexOfAny('\\', '\r') < 0) return value.ToString();
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++) {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length) {
                var next = value[++i];
                builder.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else if (c == '\r') {
                if (i + 1 < value.Length && value[i + 1] == '\n') i++;
                builder.Append('\n');
            }
            else {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Splits a still-escaped value at every unescaped <paramref name="separator"/>, unescaping each part.</summary>
    public static List<string> SplitUnescape(ReadOnlySpan<char> value, char separator) {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++) {
            if (value[i] == '\\') {
                i++;
                continue;
            }

            if (value[i] != separator) continue;
            parts.Add(Unescape(value[start..i]));
            start = i + 1;
        }

        parts.Add(Unescape(value[start..]));
        return parts;
    }

    /// <summary>Splits a still-escaped value at every unescaped <paramref name="separator"/>, keeping the escapes.</summary>
    public static List<string> SplitRaw(string value, char separator) {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++) {
            if (value[i] == '\\') {
                i++;
                continue;
            }

            if (value[i] != separator) continue;
            parts.Add(value[start..i]);
            start = i + 1;
        }

        parts.Add(value[start..]);
        return parts;
    }

    /// <summary>Escapes a text value: backslash, semicolon, comma and line breaks.</summary>
    public static string Escape(string value) {
        if (value.AsSpan().IndexOfAny("\\;,\r\n") < 0) return value;
        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++) {
            var c = value[i];
            switch (c) {
                case '\\': builder.Append(@"\\"); break;
                case ';': builder.Append(@"\;"); break;
                case ',': builder.Append(@"\,"); break;
                case '\r':
                    if (i + 1 < value.Length && value[i + 1] == '\n') i++;
                    builder.Append(@"\n");
                    break;
                case '\n': builder.Append(@"\n"); break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats a parameter value: RFC 6868 carets for a caret, a quote and a line break, and quotes around a value
    /// holding <c>:</c>, <c>;</c> or <c>,</c> (spec 0004 FR-045).
    /// </summary>
    public static string FormatParameterValue(string value) {
        var needsQuotes = value.AsSpan().IndexOfAny(":;,") >= 0;
        if (value.AsSpan().IndexOfAny("^\"\r\n") >= 0) {
            var builder = new StringBuilder(value.Length + 8);
            for (var i = 0; i < value.Length; i++) {
                var c = value[i];
                switch (c) {
                    case '^': builder.Append("^^"); break;
                    case '"': builder.Append("^'"); break;
                    case '\r':
                        if (i + 1 < value.Length && value[i + 1] == '\n') i++;
                        builder.Append("^n");
                        break;
                    case '\n': builder.Append("^n"); break;
                    default: builder.Append(c); break;
                }
            }

            value = builder.ToString();
        }

        return needsQuotes ? "\"" + value + "\"" : value;
    }
}
