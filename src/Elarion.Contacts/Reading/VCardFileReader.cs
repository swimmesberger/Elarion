using System.Globalization;
using System.Text;
using Elarion.ContentLines;

namespace Elarion.Contacts.Reading;

/// <summary>
/// The card state machine over the shared content-line layer (FR-003–FR-019, FR-033–FR-039, FR-047–FR-049, FR-073,
/// FR-074): one pass, no recursion, every line counted against its card and the file.
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
        var rules = ContentLineRules.VCard(
            lineLimit, line => IsPhotoLine(line) ? Math.Max(photoLineLimit, lineLimit) : lineLimit, options.MaxParametersPerProperty);

        var entries = new List<ContactEntry>();
        var fileErrors = new List<ContactError>();
        var state = new ReadState(options);
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

                var isTooLong = reader.IsTooLong;
                ParsedContentLine parsed = default;
                var tooManyParameters = false;
                var isParsed = !isTooLong && ContentLineParser.TryParse(line, rules, decoder, out parsed, out tooManyParameters);

                if (isParsed && parsed.Name == "BEGIN" && IsVCard(parsed.Value)) {
                    sawBegin = true;
                    if (card is not null) {
                        if (card.AgentPending) {
                            card.StartAgent();
                            continue;
                        }

                        card.Errors.Add(UnexpectedEnd());
                        entries.Add(card.Close(options, state));
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
                if (isParsed && parsed.Name == "END" && IsVCard(parsed.Value)) {
                    entries.Add(card.Close(options, state));
                    card = null;
                    continue;
                }

                if (!card.CountLine(lineNumber, options)) continue;
                // FR-090: a parameter costs a result about what a line does, so it counts against the file's budget too.
                if (!state.CountLine(isParsed ? 1 + ParameterValues(parsed.Parameters) : 1)) {
                    // FR-073: the file-wide bound; the card being read is incomplete and is not kept.
                    fileErrors.Add(state.TooManyProperties(lineNumber));
                    card = null;
                    break;
                }

                if (isTooLong) {
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

                if (tooManyParameters) {
                    card.AgentPending = false;
                    var name = ContentLineParser.PeekName(line, allowGroups: true);
                    card.Errors.Add(new ContactError(
                        ContactErrorCodes.TooManyParameters,
                        $"The line has more than {options.MaxParametersPerProperty.ToString(CultureInfo.InvariantCulture)} parameter values; it was skipped.",
                        lineNumber: lineNumber, property: name, limit: options.MaxParametersPerProperty));
                    continue;
                }

                if (!isParsed) {
                    card.AgentPending = false;
                    card.Errors.Add(new ContactError(
                        ContactErrorCodes.MalformedLine, "The line has no property name or no colon; it was skipped.",
                        lineNumber: lineNumber, value: decoder.Decode(line)));
                    continue;
                }

                var property = ToProperty(parsed, lineNumber, decoder, state, card.IsVersion21, card.Errors);
                card.Properties.Add(property);
                if (property.Name == "VERSION" && card.Version is null) {
                    card.Version = ContentText.Unescape(property.Value).Trim();
                    if (card.IsVersion21) card.ConvertEarlierTo21Escaping();
                }

                card.AgentPending = property.Name == "AGENT" && property.Value.Trim().Length == 0;
            }
        }

        if (card is not null) {
            card.FinishAgent(lineLimit);
            card.Errors.Add(UnexpectedEnd());
            entries.Add(card.Close(options, state));
        }

        if (!sawBegin && fileErrors.Count == 0) {
            // FR-075: say what the bytes are when they are not text, so "a PDF" and "a text without a vCard" word apart.
            var mediaType = fileEncoding.Transcoded ? null : FileSniffer.BinaryMediaType(input.Span);
            fileErrors.Add(mediaType is null
                ? new ContactError(ContactErrorCodes.Unreadable, "The file holds no vCard.")
                : new ContactError(ContactErrorCodes.Unreadable, $"The file is not text ({mediaType}); it holds no vCard.", mediaType: mediaType));
        }

        if (state.OmittedErrors > 0) {
            fileErrors.Add(new ContactError(
                ContactErrorCodes.TooManyErrors,
                $"The file has {state.OmittedErrors.ToString(CultureInfo.InvariantCulture)} more problems than the {options.MaxErrors.ToString(CultureInfo.InvariantCulture)} listed; they are not listed.",
                limit: options.MaxErrors));
        }

        return new ContactReadResult(entries, fileErrors, fileEncoding.ByteOrderMarkEncoding, decoder.UsedFallbackEncoding, state.OmittedErrors);
    }

    private static int ParameterValues(List<ContentParameter> parameters) {
        var count = 0;
        foreach (var parameter in parameters) count += Math.Max(1, parameter.Values.Count);
        return count;
    }

    private static ContactError UnexpectedEnd() =>
        new(ContactErrorCodes.UnexpectedEnd, "The vCard is not closed by END:VCARD.");

    private static ContactProperty ToProperty(
        ParsedContentLine parsed, int lineNumber, ContentTextDecoder decoder, ReadState state, bool version21, List<ContactError> errors) {
        // Names and short values repeat on every card: one instance of each per read, and no array for no parameters.
        var parameters = parsed.Parameters.Count == 0 ? [] : new ContactParameter[parsed.Parameters.Count];
        string? encoding = null, charset = null;
        for (var i = 0; i < parameters.Length; i++) {
            var parameter = parsed.Parameters[i];
            parameters[i] = state.Parameter(parameter.Name, parameter.Values);
            if (parameter.Values.Count == 0) continue;
            if (parameter.Name == "ENCODING") encoding ??= parameter.Values[0];
            else if (parameter.Name == "CHARSET") charset ??= parameter.Values[0];
        }

        string value;
        bool unknownCharset;
        if (encoding is not null && encoding.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)) {
            var bytes = DecodeQuotedPrintable(parsed.Value);
            var text = decoder.Decode(bytes, charset, out unknownCharset);
            value = version21 ? EscapeVersion21(text) : EscapeLineBreaks(text);
        }
        else if (encoding is not null && (encoding.Equals("B", StringComparison.OrdinalIgnoreCase) || encoding.Equals("BASE64", StringComparison.OrdinalIgnoreCase))) {
            value = Encoding.Latin1.GetString(parsed.Value);
            unknownCharset = false;
        }
        else {
            var text = decoder.Decode(parsed.Value, charset, out unknownCharset);
            value = version21 ? EscapeVersion21(text) : text;
        }

        if (unknownCharset) {
            errors.Add(new ContactError(
                ContactErrorCodes.UnknownCharset, $"The charset '{charset}' is not known; the value was decoded without it.",
                lineNumber: lineNumber, property: parsed.Name, value: value, charset: charset));
        }

        return new ContactProperty(state.Name(parsed.Group), state.Name(parsed.Name)!, parameters, state.ShortValue(value)) { LineNumber = lineNumber };
    }

    /// <summary>
    /// A vCard 2.1 value in the escaping of 3.0 and 4.0 (FR-072). 2.1 escapes only the semicolon, so every other
    /// backslash is a character and is doubled, and a line break of a quoted-printable value becomes the <c>\n</c>
    /// escape. The mapper and the writer then treat the value like any other: <c>N:C:\Daten</c> reads <c>C:\Daten</c>.
    /// </summary>
    /// <param name="text">The decoded 2.1 value.</param>
    /// <param name="keepLineBreakEscapes">
    /// Whether a <c>\n</c> already stands for a line break: for a value read before the card's <c>VERSION:2.1</c>, whose
    /// quoted-printable line breaks were escaped by the 3.0 rule.
    /// </param>
    internal static string EscapeVersion21(string text, bool keepLineBreakEscapes = false) {
        if (text.AsSpan().IndexOfAny('\\', '\r', '\n') < 0) return text;
        var builder = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '\\') {
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (next == ';' || (keepLineBreakEscapes && next is 'n' or 'N')) {
                    builder.Append(c).Append(next);
                    i++;
                }
                else {
                    builder.Append(@"\\");
                }
            }
            else if (c == '\r') {
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

    /// <summary>
    /// The state of one read: the file-wide line and error budgets (FR-073, FR-074), and the names and short values
    /// every card of a file repeats, held once, so a property costs its own object and not a copy of each string.
    /// </summary>
    private sealed class ReadState(ContactReadOptions options) {
        // Enough for every name and common value of a real file; a hostile file of distinct names stops sharing there.
        private const int MaxShared = 4096;
        private const int MaxSharedValueLength = 32;

        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Name, string Value), ContactParameter> _parameters = [];
        private long _lines;
        private int _errors;

        /// <summary>The errors over <see cref="ContactReadOptions.MaxErrors"/>, counted and not kept.</summary>
        public int OmittedErrors { get; private set; }

        /// <summary>Counts a line of a card and its parameter values (<paramref name="weight"/>) against <see cref="ContactReadOptions.MaxProperties"/>.</summary>
        public bool CountLine(int weight) => (_lines += weight) <= options.MaxProperties;

        public ContactError TooManyProperties(int lineNumber) =>
            new(ContactErrorCodes.TooManyPropertiesInFile,
                $"The file holds more than {options.MaxProperties.ToString(CultureInfo.InvariantCulture)} properties and parameters; the contact being read and the rest were not read.",
                lineNumber: lineNumber, limit: options.MaxProperties);

        /// <summary>Keeps as many of <paramref name="errors"/> as <see cref="ContactReadOptions.MaxErrors"/> still allows and counts the rest.</summary>
        public List<ContactError> Keep(List<ContactError> errors) {
            var room = Math.Max(0, options.MaxErrors - _errors);
            if (errors.Count <= room) {
                _errors += errors.Count;
                return errors;
            }

            _errors += room;
            OmittedErrors += errors.Count - room;
            return errors.GetRange(0, room);
        }

        public string? Name(string? name) => name is null ? null : Share(name);

        public string ShortValue(string value) => value.Length <= MaxSharedValueLength ? Share(value) : value;

        public ContactParameter Parameter(string name, List<string> values) {
            if (values.Count == 0) return new ContactParameter(Share(name), []);
            if (values.Count > 1 || values[0].Length > MaxSharedValueLength) {
                var copy = new string[values.Count];
                for (var i = 0; i < copy.Length; i++) copy[i] = ShortValue(values[i]);
                return new ContactParameter(Share(name), copy);
            }

            if (_parameters.TryGetValue((name, values[0]), out var shared)) return shared;
            var parameter = new ContactParameter(Share(name), [Share(values[0])]);
            if (_parameters.Count < MaxShared) _parameters[(parameter.Name, parameter.Values[0])] = parameter;
            return parameter;
        }

        private string Share(string value) {
            if (value.Length == 0) return "";
            if (_strings.TryGetValue(value, out var shared)) return shared;
            if (_strings.Count < MaxShared) _strings[value] = value;
            return value;
        }
    }

    /// <summary>The state of the card being read.</summary>
    private sealed class Card(int index, int lineNumber) {
        // What a card over MaxPropertiesPerContact keeps of its properties: enough to name it in a preview.
        private static readonly string[] DisplayProperties = ["FN", "N", "ORG", "EMAIL", "TEL", "UID"];

        private int _lines;
        private bool _tooMany;
        private StringBuilder? _agent;
        private bool _agentTooLong;

        public List<ContactProperty> Properties { get; private set; } = [];

        public List<ContactError> Errors { get; } = [];

        public string? Version { get; set; }

        public bool IsVersion21 => Version == "2.1";

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

        /// <summary>
        /// The properties read before a <c>VERSION:2.1</c> took the 3.0 escaping; they take 2.1's now. A line break of a
        /// quoted-printable value among them already reads as <c>\n</c> and stays one.
        /// </summary>
        public void ConvertEarlierTo21Escaping() {
            for (var i = 0; i < Properties.Count - 1; i++) {
                var property = Properties[i];
                if (IsBase64(property)) continue;
                var value = EscapeVersion21(property.Value, keepLineBreakEscapes: true);
                if (!ReferenceEquals(value, property.Value)) Properties[i] = property with { Value = value };
            }
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

        public ContactEntry Close(ContactReadOptions options, ReadState state) {
            FinishAgent(options.MaxLineBytes);
            var errors = Errors;
            Contact? contact = null;
            if (!_tooMany) contact = ContactMapper.Map(Properties, options.MaxPhotoBytes, errors);
            else Properties = KeepForDisplay(Properties);

            // File order: errors with a line by that line, the card's own (empty, unexpected end) last.
            var located = errors
                .Select((error, order) => (error, order))
                .OrderBy(static pair => pair.error.LineNumber ?? int.MaxValue)
                .ThenBy(static pair => pair.order)
                .Select(pair => pair.error.At(index, lineNumber, contact?.DisplayName))
                .ToList();
            return new ContactEntry(index, lineNumber, Version ?? DefaultVersion, Properties, contact, state.Keep(located));
        }

        // A card over the limit has no contact; keeping all its lines would let a file of such cards hold every line it
        // has (FR-048). The first formatted name, name, organization, e-mail address, phone number and UID stay, to name it.
        private static List<ContactProperty> KeepForDisplay(List<ContactProperty> properties) {
            var kept = new List<ContactProperty>(DisplayProperties.Length);
            foreach (var name in DisplayProperties) {
                var first = properties.Find(property => property.Name == name);
                if (first is not null) kept.Add(first);
            }

            kept.Sort(static (a, b) => (a.LineNumber ?? 0).CompareTo(b.LineNumber ?? 0));
            return kept;
        }

        private static bool IsBase64(ContactProperty property) =>
            property.GetParameter("ENCODING").Any(static value => value.Equals("B", StringComparison.OrdinalIgnoreCase) || value.Equals("BASE64", StringComparison.OrdinalIgnoreCase));
    }
}
