using System.Text;
using System.Xml;

namespace Elarion.WebDav;

/// <summary>
/// An element of a parsed WebDAV body: its name, attributes, child elements and text. Request and response bodies of
/// WebDAV are small trees, so the hardened reader (<see cref="DavXml.Parse"/>) builds one instead of streaming.
/// </summary>
public sealed class DavXmlElement {
    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();

    /// <summary>Creates an element.</summary>
    public DavXmlElement(DavName name, IReadOnlyList<DavXmlElement> children, string text,
        IReadOnlyDictionary<string, string>? attributes = null) {
        Name = name;
        Children = children;
        Text = text;
        Attributes = attributes ?? NoAttributes;
    }

    /// <summary>The element's qualified name.</summary>
    public DavName Name { get; }

    /// <summary>The attributes without a namespace, by local name.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; }

    /// <summary>The child elements, in document order.</summary>
    public IReadOnlyList<DavXmlElement> Children { get; }

    /// <summary>The element's own text (text and CDATA nodes directly inside it), concatenated.</summary>
    public string Text { get; }

    /// <summary>Whether the element is <paramref name="name"/>.</summary>
    public bool Is(DavName name) => Name == name;

    /// <summary>The first child named <paramref name="name"/>, or <see langword="null"/>.</summary>
    public DavXmlElement? Child(DavName name) {
        foreach (var child in Children)
            if (child.Name == name)
                return child;
        return null;
    }

    /// <summary>The children named <paramref name="name"/>.</summary>
    public IEnumerable<DavXmlElement> ChildrenNamed(DavName name) => Children.Where(child => child.Name == name);

    /// <summary>The attribute <paramref name="localName"/>, or <see langword="null"/>.</summary>
    public string? Attribute(string localName) => Attributes.GetValueOrDefault(localName);

    /// <inheritdoc />
    public override string ToString() => Name.ToString();
}

/// <summary>Why a body could not be parsed.</summary>
public enum DavXmlErrorKind {
    /// <summary>The body is not well-formed XML, or holds a DTD.</summary>
    Malformed,

    /// <summary>The body is larger than the byte limit, or holds more elements than the element limit.</summary>
    TooLarge,

    /// <summary>The body nests elements deeper than the depth limit.</summary>
    TooDeep,
}

/// <summary>The limits of a parse: bytes, nesting depth and element count.</summary>
/// <param name="MaxBytes">The most bytes (characters) the document may hold.</param>
/// <param name="MaxDepth">The deepest element nesting.</param>
/// <param name="MaxElements">The most elements.</param>
public readonly record struct DavXmlLimits(long MaxBytes, int MaxDepth, int MaxElements);

/// <summary>The result of parsing a WebDAV body: the root element, or the reason it has none.</summary>
public sealed class DavXmlParseResult {
    private DavXmlParseResult(DavXmlElement? root, DavXmlErrorKind? error, string? message) {
        Root = root;
        Error = error;
        Message = message;
    }

    /// <summary>The root element, when the body parsed.</summary>
    public DavXmlElement? Root { get; }

    /// <summary>Why the body did not parse, or <see langword="null"/>.</summary>
    public DavXmlErrorKind? Error { get; }

    /// <summary>The English description of <see cref="Error"/>.</summary>
    public string? Message { get; }

    internal static DavXmlParseResult Success(DavXmlElement root) => new(root, null, null);

    internal static DavXmlParseResult Failure(DavXmlErrorKind kind, string message) => new(null, kind, message);
}

/// <summary>
/// The hardened XML reader and the writer settings of WebDAV bodies (spec 0006 FR-025, FR-043): DTDs are refused, no
/// resolver is used so no external entity is fetched, and the size, depth and element count are bounded.
/// </summary>
public static class DavXml {
    // Fixed prefixes keep the written XML readable in tests and diffs.
    internal const string DavPrefix = "D";
    internal const string CalDavPrefix = "C";
    internal const string CalendarServerPrefix = "CS";
    internal const string AppleIcalPrefix = "ICAL";
    internal const string CardDavPrefix = "CARD";

    /// <summary>
    /// Parses <paramref name="body"/>. Never throws for what the body holds: a malformed, oversized or too deep
    /// document is a result with <see cref="DavXmlParseResult.Error"/>.
    /// </summary>
    /// <param name="body">The whole body, already bounded by the caller's byte limit.</param>
    /// <param name="limits">The limits.</param>
    public static DavXmlParseResult Parse(ReadOnlyMemory<byte> body, DavXmlLimits limits) {
        if (body.Length > limits.MaxBytes)
            return DavXmlParseResult.Failure(DavXmlErrorKind.TooLarge, $"The body is larger than {limits.MaxBytes} bytes.");
        var settings = new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = Math.Max(1, limits.MaxBytes),
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true,
        };

        try {
            using var stream = new ReadOnlyMemoryStream(body);
            using var reader = XmlReader.Create(stream, settings);
            return Read(reader, limits);
        }
        catch (XmlException ex) {
            return DavXmlParseResult.Failure(DavXmlErrorKind.Malformed, "The body is not well-formed XML: " + ex.Message);
        }
        catch (InvalidOperationException ex) {
            return DavXmlParseResult.Failure(DavXmlErrorKind.Malformed, "The body is not well-formed XML: " + ex.Message);
        }
    }

    private static DavXmlParseResult Read(XmlReader reader, DavXmlLimits limits) {
        var stack = new Stack<Builder>();
        DavXmlElement? root = null;
        var elements = 0;
        while (reader.Read()) {
            switch (reader.NodeType) {
                case XmlNodeType.Element: {
                    if (root is not null) return DavXmlParseResult.Failure(DavXmlErrorKind.Malformed, "The body holds more than one root element.");
                    if (++elements > limits.MaxElements)
                        return DavXmlParseResult.Failure(DavXmlErrorKind.TooLarge, $"The body holds more than {limits.MaxElements} elements.");
                    if (stack.Count + 1 > limits.MaxDepth)
                        return DavXmlParseResult.Failure(DavXmlErrorKind.TooDeep, $"The body nests elements deeper than {limits.MaxDepth}.");
                    var builder = new Builder(new DavName(reader.NamespaceURI, reader.LocalName));
                    if (reader.HasAttributes) {
                        while (reader.MoveToNextAttribute()) {
                            if (reader.NamespaceURI.Length == 0) (builder.Attributes ??= new Dictionary<string, string>(StringComparer.Ordinal))[reader.LocalName] = reader.Value;
                        }

                        reader.MoveToElement();
                    }

                    if (reader.IsEmptyElement) {
                        var element = builder.Build();
                        if (stack.Count == 0) root = element;
                        else stack.Peek().Children.Add(element);
                    }
                    else {
                        stack.Push(builder);
                    }

                    break;
                }
                case XmlNodeType.EndElement: {
                    var element = stack.Pop().Build();
                    if (stack.Count == 0) root = element;
                    else stack.Peek().Children.Add(element);
                    break;
                }
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    if (stack.Count > 0) (stack.Peek().Text ??= new StringBuilder()).Append(reader.Value);
                    break;
                case XmlNodeType.DocumentType:
                    return DavXmlParseResult.Failure(DavXmlErrorKind.Malformed, "The body holds a DTD.");
            }
        }

        return root is null
            ? DavXmlParseResult.Failure(DavXmlErrorKind.Malformed, "The body holds no element.")
            : DavXmlParseResult.Success(root);
    }

    /// <summary>The settings of every XML writer of a WebDAV body: UTF-8 without a BOM, carriage returns kept as entities.</summary>
    internal static XmlWriterSettings WriterSettings { get; } = new() {
        Encoding = new UTF8Encoding(false),
        Indent = false,
        CloseOutput = false,
        // Calendar data carries CRLF line ends; an XML parser would normalise a literal CR away, so it is written as
        // an entity and the client reads back the bytes the server stored.
        NewLineHandling = NewLineHandling.Entitize,
        Async = false,
    };

    /// <summary>Writes the namespace declarations of the fixed prefixes on the current element.</summary>
    internal static void DeclarePrefixes(XmlWriter writer) {
        writer.WriteAttributeString("xmlns", DavPrefix, null, DavNames.Dav);
        writer.WriteAttributeString("xmlns", CalDavPrefix, null, DavNames.CalDav);
        writer.WriteAttributeString("xmlns", CalendarServerPrefix, null, DavNames.CalendarServer);
        writer.WriteAttributeString("xmlns", AppleIcalPrefix, null, DavNames.AppleIcal);
        writer.WriteAttributeString("xmlns", CardDavPrefix, null, DavNames.CardDav);
    }

    /// <summary>Starts <paramref name="name"/> with the fixed prefix of its namespace, or a generated one.</summary>
    internal static void StartElement(XmlWriter writer, DavName name) {
        var prefix = name.Namespace switch {
            DavNames.Dav => DavPrefix,
            DavNames.CalDav => CalDavPrefix,
            DavNames.CalendarServer => CalendarServerPrefix,
            DavNames.AppleIcal => AppleIcalPrefix,
            DavNames.CardDav => CardDavPrefix,
            _ => null,
        };
        if (prefix is not null) writer.WriteStartElement(prefix, name.LocalName, name.Namespace);
        else writer.WriteStartElement(name.LocalName, name.Namespace);
    }

    /// <summary>Writes an empty element.</summary>
    internal static void EmptyElement(XmlWriter writer, DavName name) {
        StartElement(writer, name);
        writer.WriteEndElement();
    }

    /// <summary>Writes an element holding text.</summary>
    internal static void TextElement(XmlWriter writer, DavName name, string text) {
        StartElement(writer, name);
        writer.WriteString(text);
        writer.WriteEndElement();
    }

    private sealed class Builder(DavName name) {
        public List<DavXmlElement> Children { get; } = [];

        public StringBuilder? Text { get; set; }

        public Dictionary<string, string>? Attributes { get; set; }

        public DavXmlElement Build() => new(name, Children, Text?.ToString() ?? "", Attributes);
    }

    private sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> memory) : Stream {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => memory.Length;

        public override long Position {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) {
            var count = Math.Min(buffer.Length, memory.Length - _position);
            memory.Span.Slice(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
