using System.Globalization;
using System.Text;
using Elarion.ContentLines;

namespace Elarion.Contacts.Reading;

/// <summary>
/// The card state machine over the shared content-line layer (FR-003–FR-019, FR-033–FR-039, FR-047–FR-049): one pass,
/// no recursion, every line counted against its card.
/// </summary>
internal static class VCardFileReader {
    private const string DefaultVersion = "3.0";

    public static ContactReadResult Read(ReadOnlyMemory<byte> input, ContactReadOptions options, Encoding fallback, CancellationToken cancellationToken) {
        var fileEncoding = ContentTextDecoder.Detect(input.Span, out var transcoded);
        var data = transcoded is not null ? transcoded.AsMemory() : input[fileEncoding.Skip..];
        var decoder = new ContentTextDecoder(fallback, fileEncoding.Transcoded, useCharset: true);

        // A photo line may hold the Base64 of MaxPhotoBytes plus its name, parameters and folding slack (FR-050).
        var photoLineLimit = (int)Math.Min(int.MaxValue - 1, (options.MaxPhotoBytes + 2L) / 3 * 4 + 64 * 1024);
        var lineLimit = options.MaxLineBytes;
        var rules = ContentLineRules.VCard(lineLimit, line => IsPhotoLine(line) ? Math.Max(photoLineLimit, lineLimit) : lineLimit);

        var entries = new List<ContactEntry>();
        var fileErrors = new List<ContactError>();
        var sawBegin = false;
        Card? card = null;

        using (var reader = new ContentLineReader(data, rules)) {
            while (reader.Read()) {
                var line = reader.Current;
                var lineNumber = reader.LineNumber;

                if (card is { AgentDepth: > 0 }) {
                    card.ContinueAgent(line, reader.IsTooLong, rules, decoder, lineLimit);
                    continue;
                }

                if (reader.IsTooLong) {
                    if (card is null || !card.CountLine(lineNumber, options)) continue;
                    card.AgentPending = false;
                    var name = ContentLineParser.PeekName(line, allowGroups: true) ?? "";
                    card.Errors.Add(name == "PHOTO"
                        ? new ContactError(
                            ContactErrorCodes.PhotoTooLarge,
                            $"The photo is larger than {options.MaxPhotoBytes.ToString(CultureInfo.InvariantCulture)} bytes.",
                            lineNumber: lineNumber, property: name, limit: options.MaxPhotoBytes)
                        : new ContactError(
                            ContactErrorCodes.LineTooLong,
                            $"The line is longer than {reader.Limit.ToString(CultureInfo.InvariantCulture)} bytes.",
                            lineNumber: lineNumber, property: name.Length == 0 ? null : name, limit: reader.Limit));
                    continue;
                }

                if (!ContentLineParser.TryParse(line, rules, decoder, out var parsed)) {
                    if (card is null || !card.CountLine(lineNumber, options)) continue;
                    card.AgentPending = false;
                    card.Errors.Add(new ContactError(
                        ContactErrorCodes.MalformedLine, "The line has no property name or no colon; it was skipped.",
                        lineNumber: lineNumber, value: decoder.Decode(line)));
                    continue;
                }

                if (parsed.Name == "BEGIN" && IsVCard(parsed.Value)) {
                    sawBegin = true;
                    if (card is not null) {
                        if (card.AgentPending) {
                            card.StartAgent();
                            continue;
                        }

                        card.Errors.Add(UnexpectedEnd());
                        entries.Add(card.Close(options));
                        card = null;
                    }

                    if (entries.Count >= options.MaxContacts) {
                        fileErrors.Add(new ContactError(
                            ContactErrorCodes.TooManyContacts,
                            $"The file holds more than {options.MaxContacts.ToString(CultureInfo.InvariantCulture)} contacts; the rest were not read.",
                            lineNumber: lineNumber, limit: options.MaxContacts));
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    card = new Card(entries.Count + 1, lineNumber);
                    continue;
                }

                if (card is null) continue;
                if (parsed.Name == "END" && IsVCard(parsed.Value)) {
                    entries.Add(card.Close(options));
                    card = null;
                    continue;
                }

                if (!card.CountLine(lineNumber, options)) continue;
                var property = ToProperty(parsed, lineNumber, decoder, card.Errors);
                card.Properties.Add(property);
                if (property.Name == "VERSION") card.Version ??= ContentText.Unescape(property.Value).Trim();
                card.AgentPending = property.Name == "AGENT" && property.Value.Trim().Length == 0;
            }
        }

        if (card is not null) {
            card.FinishAgent(lineLimit);
            card.Errors.Add(UnexpectedEnd());
            entries.Add(card.Close(options));
        }

        if (!sawBegin && fileErrors.Count == 0)
            fileErrors.Add(new ContactError(ContactErrorCodes.Unreadable, "The file holds no vCard."));

        return new ContactReadResult(entries, fileErrors, fileEncoding.ByteOrderMarkEncoding, decoder.UsedFallbackEncoding);
    }

    private static ContactError UnexpectedEnd() =>
        new(ContactErrorCodes.UnexpectedEnd, "The vCard is not closed by END:VCARD.");

    private static ContactProperty ToProperty(ParsedContentLine parsed, int lineNumber, ContentTextDecoder decoder, List<ContactError> errors) {
        var parameters = new ContactParameter[parsed.Parameters.Count];
        string? encoding = null, charset = null;
        for (var i = 0; i < parameters.Length; i++) {
            var parameter = parsed.Parameters[i];
            parameters[i] = new ContactParameter(parameter.Name, [.. parameter.Values]);
            if (parameter.Values.Count == 0) continue;
            if (parameter.Name == "ENCODING") encoding ??= parameter.Values[0];
            else if (parameter.Name == "CHARSET") charset ??= parameter.Values[0];
        }

        string value;
        bool unknownCharset;
        if (encoding is not null && encoding.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)) {
            var bytes = DecodeQuotedPrintable(parsed.Value);
            value = EscapeLineBreaks(decoder.Decode(bytes, charset, out unknownCharset));
        }
        else if (encoding is not null && (encoding.Equals("B", StringComparison.OrdinalIgnoreCase) || encoding.Equals("BASE64", StringComparison.OrdinalIgnoreCase))) {
            value = Encoding.Latin1.GetString(parsed.Value);
            unknownCharset = false;
        }
        else {
            value = decoder.Decode(parsed.Value, charset, out unknownCharset);
        }

        if (unknownCharset) {
            errors.Add(new ContactError(
                ContactErrorCodes.UnknownCharset, $"The charset '{charset}' is not known; the value was decoded without it.",
                lineNumber: lineNumber, property: parsed.Name, value: value, charset: charset));
        }

        return new ContactProperty(parsed.Group, parsed.Name, parameters, value) { LineNumber = lineNumber };
    }

    // Quoted-printable (FR-010); soft breaks were joined by the line reader. An invalid escape keeps its characters.
    private static byte[] DecodeQuotedPrintable(ReadOnlySpan<byte> value) {
        var bytes = new byte[value.Length];
        var length = 0;
        for (var i = 0; i < value.Length; i++) {
            var b = value[i];
            if (b == (byte)'=' && i + 2 < value.Length && IsHex(value[i + 1]) && IsHex(value[i + 2])) {
                bytes[length++] = (byte)(Hex(value[i + 1]) << 4 | Hex(value[i + 2]));
                i += 2;
                continue;
            }

            bytes[length++] = b;
        }

        return bytes.AsSpan(0, length).ToArray();
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F' or >= (byte)'a' and <= (byte)'f';

    private static int Hex(byte b) => b <= (byte)'9' ? b - '0' : (b | 0x20) - 'a' + 10;

    // A line break a quoted-printable value carries becomes the \n escape, so the value stays one escaped line.
    private static string EscapeLineBreaks(string text) {
        if (text.AsSpan().IndexOfAny('\r', '\n') < 0) return text;
        var builder = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '\r') {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                builder.Append(@"\n");
            }
            else if (c == '\n') {
                builder.Append(@"\n");
            }
            else {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool IsVCard(ReadOnlySpan<byte> value) => value.Trim(" \t"u8).SequenceEqual("VCARD"u8) || EqualsIgnoreCase(value.Trim(" \t"u8), "VCARD"u8);

    private static bool EqualsIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> upper) {
        if (value.Length != upper.Length) return false;
        for (var i = 0; i < value.Length; i++) {
            var b = value[i];
            if (b is >= (byte)'a' and <= (byte)'z') b -= 32;
            if (b != upper[i]) return false;
        }

        return true;
    }

    // [group.]PHOTO followed by ';' or ':' (ignoring case): the one property whose line limit is the photo's.
    private static bool IsPhotoLine(ReadOnlySpan<byte> line) {
        var i = 0;
        while (i < line.Length && line[i] is (byte)' ' or (byte)'\t') i++;
        var start = i;
        while (i < line.Length && line[i] is not ((byte)';' or (byte)':')) {
            if (line[i] == (byte)'.') start = i + 1;
            i++;
            if (i - start > 64) return false;
        }

        return EqualsIgnoreCase(line[start..i].TrimEnd(" \t"u8), "PHOTO"u8);
    }

    /// <summary>The state of the card being read.</summary>
    private sealed class Card(int index, int lineNumber) {
        private int _lines;
        private bool _tooMany;
        private StringBuilder? _agent;
        private bool _agentTooLong;

        public List<ContactProperty> Properties { get; } = [];

        public List<ContactError> Errors { get; } = [];

        public string? Version { get; set; }

        // An AGENT with an empty value whose vCard follows on its own lines (vCard 2.1): kept as text, never parsed (FR-033).
        public bool AgentPending { get; set; }

        public int AgentDepth { get; private set; }

        /// <summary>Counts a line against the limit (FR-048); <see langword="false"/> once the card is over it.</summary>
        public bool CountLine(int line, ContactReadOptions options) {
            if (_tooMany) return false;
            if (++_lines <= options.MaxPropertiesPerContact) return true;

            _tooMany = true;
            Errors.Add(new ContactError(
                ContactErrorCodes.TooManyProperties,
                $"The vCard has more than {options.MaxPropertiesPerContact.ToString(CultureInfo.InvariantCulture)} lines; it was not read.",
                lineNumber: line, limit: options.MaxPropertiesPerContact));
            return false;
        }

        public void StartAgent() {
            AgentPending = false;
            AgentDepth = 1;
            _agent = new StringBuilder("BEGIN:VCARD");
        }

        public void ContinueAgent(ReadOnlySpan<byte> line, bool tooLong, ContentLineRules rules, ContentTextDecoder decoder, int limit) {
            if (ContentLineParser.TryParse(line, rules, decoder, out var parsed) && IsVCard(parsed.Value)) {
                if (parsed.Name == "BEGIN") AgentDepth++;
                else if (parsed.Name == "END") AgentDepth--;
            }

            if (tooLong) _agentTooLong = true;
            if (!_agentTooLong) {
                var text = decoder.Decode(line);
                if (_agent!.Length + text.Length + 1 > limit) _agentTooLong = true;
                else _agent.Append('\n').Append(text);
            }

            if (AgentDepth == 0) FinishAgent(limit);
        }

        public void FinishAgent(int limit) {
            if (_agent is null) return;
            var at = Properties.FindLastIndex(static property => property.Name == "AGENT");
            var agent = Properties[at];
            if (_agentTooLong) {
                Properties.RemoveAt(at);
                Errors.Add(new ContactError(
                    ContactErrorCodes.LineTooLong, $"The line is longer than {limit.ToString(CultureInfo.InvariantCulture)} bytes.",
                    lineNumber: agent.LineNumber, property: agent.Name, limit: limit));
            }
            else {
                Properties[at] = agent with { Value = ContentText.Escape(_agent.ToString()) };
            }

            _agent = null;
            AgentDepth = 0;
        }

        public ContactEntry Close(ContactReadOptions options) {
            FinishAgent(options.MaxLineBytes);
            var errors = Errors;
            Contact? contact = null;
            if (!_tooMany) contact = ContactMapper.Map(Properties, options.MaxPhotoBytes, errors);

            // File order: errors with a line by that line, the card's own (empty, unexpected end) last.
            var located = errors
                .Select((error, order) => (error, order))
                .OrderBy(static pair => pair.error.LineNumber ?? int.MaxValue)
                .ThenBy(static pair => pair.order)
                .Select(pair => pair.error.At(index, lineNumber, contact?.DisplayName));
            return new ContactEntry(index, lineNumber, Version ?? DefaultVersion, Properties, contact, located);
        }
    }
}
