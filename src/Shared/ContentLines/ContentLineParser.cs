using System.Text;

namespace Elarion.ContentLines;

/// <summary>One parameter of a content line, its values decoded (quotes removed, RFC 6868 carets decoded).</summary>
internal sealed record ContentParameter(string Name, List<string> Values);

/// <summary>
/// A tokenized content line: group, name and parameters decoded, the value still as bytes, because the format decides
/// how they decode (quoted-printable, Base64, <c>CHARSET</c>).
/// </summary>
internal readonly ref struct ParsedContentLine {
    public ParsedContentLine(string? group, string name, List<ContentParameter> parameters, ReadOnlySpan<byte> value) {
        Group = group;
        Name = name;
        Parameters = parameters;
        Value = value;
    }

    public string? Group { get; }

    /// <summary>The name in upper case.</summary>
    public string Name { get; }

    public List<ContentParameter> Parameters { get; }

    public ReadOnlySpan<byte> Value { get; }
}

/// <summary>
/// The hand-written tokenizer of content lines (spec 0004 FR-007, FR-008; spec 0005 FR-004, FR-007, FR-008): names in
/// any case, spaces around delimiters, quoted parameter values, comma lists, RFC 6868 carets, and with the vCard rule
/// set groups and bare parameters. Delimiters are ASCII bytes below 0x40, so the structure is found before decoding
/// in UTF-8 and in every legacy encoding the ladder allows.
/// </summary>
internal static class ContentLineParser {
    /// <summary>Tokenizes <paramref name="line"/>; <see langword="false"/> for a line without a name or a colon.</summary>
    public static bool TryParse(ReadOnlySpan<byte> line, ContentLineRules rules, ContentTextDecoder decoder, out ParsedContentLine parsed) =>
        TryParse(line, rules, decoder, out parsed, out _);

    /// <summary>
    /// Tokenizes <paramref name="line"/>; <see langword="false"/> for a line without a name or a colon, or with more
    /// parameter values than <see cref="ContentLineRules.MaxParameters"/>, which <paramref name="tooManyParameters"/> tells
    /// apart.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> line, ContentLineRules rules, ContentTextDecoder decoder, out ParsedContentLine parsed,
        out bool tooManyParameters) {
        parsed = default;
        tooManyParameters = false;
        var i = SkipSpaces(line, 0);
        var nameStart = i;
        while (i < line.Length && IsNameByte(line[i])) i++;
        if (i == nameStart) return false;

        var fullName = Ascii(line[nameStart..i]);
        string? group = null;
        var name = fullName;
        if (rules.AllowGroups) {
            var dot = fullName.LastIndexOf('.');
            if (dot > 0 && dot < fullName.Length - 1) {
                group = fullName[..dot];
                name = fullName[(dot + 1)..];
            }
        }

        if (name.Length == 0) return false;
        var parameters = new List<ContentParameter>();
        var parameterValues = 0;
        i = SkipSpaces(line, i);
        while (i < line.Length && line[i] == (byte)';') {
            i = SkipSpaces(line, i + 1);
            if (i < line.Length && line[i] is (byte)';' or (byte)':') continue;
            var paramStart = i;
            while (i < line.Length && IsNameByte(line[i])) i++;
            if (i == paramStart) return false;
            var paramName = Ascii(line[paramStart..i]).ToUpperInvariant();
            i = SkipSpaces(line, i);
            if (i < line.Length && line[i] == (byte)'=') {
                var values = new List<string>();
                i = SkipSpaces(line, i + 1);
                while (true) {
                    if (++parameterValues > rules.MaxParameters) {
                        tooManyParameters = true;
                        return false;
                    }

                    if (i < line.Length && line[i] == (byte)'"') {
                        var close = line[(i + 1)..].IndexOf((byte)'"');
                        if (close < 0) return false;
                        values.Add(DecodeCarets(decoder.Decode(line.Slice(i + 1, close))));
                        i = SkipSpaces(line, i + close + 2);
                    }
                    else {
                        var valueStart = i;
                        while (i < line.Length && line[i] is not ((byte)',' or (byte)';' or (byte)':')) i++;
                        values.Add(DecodeCarets(decoder.Decode(line[valueStart..i].Trim(" \t"u8))));
                    }

                    if (i < line.Length && line[i] == (byte)',') {
                        i = SkipSpaces(line, i + 1);
                        continue;
                    }

                    break;
                }

                parameters.Add(new ContentParameter(paramName, values));
            }
            else if (++parameterValues > rules.MaxParameters) {
                tooManyParameters = true;
                return false;
            }
            else if (rules.AllowBareParameters) {
                // vCard 2.1: TEL;WORK;VOICE and ADR;QUOTED-PRINTABLE.
                var kind = paramName is "QUOTED-PRINTABLE" or "BASE64" or "B" or "8BIT" or "7BIT" ? "ENCODING" : "TYPE";
                parameters.Add(new ContentParameter(kind, [paramName]));
            }
            else {
                parameters.Add(new ContentParameter(paramName, []));
            }
        }

        if (i >= line.Length || line[i] != (byte)':') return false;
        parsed = new ParsedContentLine(group, name.ToUpperInvariant(), parameters, line[(i + 1)..]);
        return true;
    }

    /// <summary>The name of a line that could not be kept, for an error; <see langword="null"/> when it has none.</summary>
    public static string? PeekName(ReadOnlySpan<byte> line, bool allowGroups) {
        var i = SkipSpaces(line, 0);
        var start = i;
        while (i < line.Length && IsNameByte(line[i])) i++;
        if (i == start) return null;
        var name = Ascii(line[start..i]);
        if (allowGroups) {
            var dot = name.LastIndexOf('.');
            if (dot > 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        }

        return name.ToUpperInvariant();
    }

    /// <summary>Whether <paramref name="name"/> is a valid property or parameter name to write.</summary>
    public static bool IsValidName(string name, bool allowDotAndUnderscore = false) {
        if (name.Length == 0) return false;
        foreach (var c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || (allowDotAndUnderscore && c is '_' or '.' or '/'))) return false;

        return true;
    }

    /// <summary>Decodes RFC 6868 carets: <c>^n</c> a line break, <c>^'</c> a double quote, <c>^^</c> a caret.</summary>
    public static string DecodeCarets(string value) {
        if (!value.Contains('^')) return value;
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++) {
            if (value[i] == '^' && i + 1 < value.Length) {
                var next = value[i + 1];
                if (next is 'n' or 'N') {
                    builder.Append('\n');
                    i++;
                    continue;
                }

                if (next == '\'') {
                    builder.Append('"');
                    i++;
                    continue;
                }

                if (next == '^') {
                    builder.Append('^');
                    i++;
                    continue;
                }
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static int SkipSpaces(ReadOnlySpan<byte> line, int i) {
        while (i < line.Length && line[i] is (byte)' ' or (byte)'\t') i++;
        return i;
    }

    private static bool IsNameByte(byte b) =>
        b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
            or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'/';

    private static string Ascii(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes);
}
