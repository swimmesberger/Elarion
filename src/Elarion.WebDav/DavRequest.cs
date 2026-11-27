using System.Globalization;
using System.Text;
using System.Xml;

namespace Elarion.WebDav;

/// <summary>
/// A WebDAV request without a wire (ADR-0111): the method, the path below the prefix, the headers the dispatcher reads
/// and the body. The host builds it from its own request type, so the dispatcher runs without a server in tests.
/// </summary>
public sealed class DavRequest {
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    /// <summary>The HTTP method, such as <c>PROPFIND</c>.</summary>
    public required string Method { get; init; }

    /// <summary>The path below the prefix, starting with <c>/</c> or empty, still percent-encoded, such as <c>/calendars/u1/</c>.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The path of the prefix as the client sees it (the host's path base and <see cref="WebDavOptions.Prefix"/>), such as
    /// <c>/dav</c>; every <c>href</c> the dispatcher writes starts with it.
    /// </summary>
    public required string HrefBase { get; init; }

    /// <summary>The request headers the dispatcher reads (<c>Depth</c>, <c>If-Match</c>, <c>If-None-Match</c>, <c>Prefer</c>,
    /// <c>Brief</c>, <c>CalDAV-Timezones</c>), by case-insensitive name.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = NoHeaders;

    /// <summary>The request body; empty when the request has none. Read at most to <see cref="WebDavOptions.MaxBodyBytes"/> plus one byte.</summary>
    public Stream Body { get; init; } = Stream.Null;

    /// <summary>The declared body length, when the client sent one.</summary>
    public long? ContentLength { get; init; }

    /// <summary>A header's value, or <see langword="null"/>.</summary>
    public string? Header(string name) {
        if (Headers.TryGetValue(name, out var value)) return value;
        foreach (var (key, candidate) in Headers)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        return null;
    }
}

/// <summary>
/// A WebDAV response without a wire: the status, the headers and a body that writes itself forward into the host's
/// response stream, so a large <c>multistatus</c> or feed is never held in memory at once.
/// </summary>
public sealed class DavResponse {
    private const string XmlContentType = "application/xml; charset=utf-8";

    /// <summary>Creates a response.</summary>
    public DavResponse(int statusCode) {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>The response headers other than <c>Content-Type</c> and <c>Content-Length</c>.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The media type of the body, or <see langword="null"/> without one.</summary>
    public string? ContentType { get; init; }

    /// <summary>The body length when it is known before writing it.</summary>
    public long? ContentLength { get; init; }

    /// <summary>Writes the body; <see langword="null"/> for a response without one. A host skips it for <c>HEAD</c>.</summary>
    public Func<Stream, CancellationToken, Task>? Body { get; init; }

    /// <summary>Writes the body to <paramref name="destination"/>.</summary>
    public Task WriteBodyAsync(Stream destination, CancellationToken cancellationToken = default) =>
        Body is null ? Task.CompletedTask : Body(destination, cancellationToken);

    /// <summary>A response with a status and no body.</summary>
    public static DavResponse Status(int statusCode) => new(statusCode) { ContentLength = 0 };

    /// <summary>A response holding <paramref name="content"/>.</summary>
    public static DavResponse Bytes(int statusCode, ReadOnlyMemory<byte> content, string contentType) => new(statusCode) {
        ContentType = contentType,
        ContentLength = content.Length,
        Body = (stream, cancellationToken) => stream.WriteAsync(content, cancellationToken).AsTask(),
    };

    /// <summary>A plain-text response, for a refused request.</summary>
    public static DavResponse Text(int statusCode, string text) => Bytes(statusCode, Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8");

    /// <summary>An XML response written forward by <paramref name="write"/> through a <see cref="DavXmlOutput"/>.</summary>
    public static DavResponse Xml(int statusCode, Func<DavXmlOutput, CancellationToken, ValueTask> write) {
        ArgumentNullException.ThrowIfNull(write);
        return new DavResponse(statusCode) {
            ContentType = XmlContentType,
            Body = async (stream, cancellationToken) => {
                await using var output = new DavXmlOutput(stream);
                await write(output, cancellationToken).ConfigureAwait(false);
                await output.CompleteAsync(cancellationToken).ConfigureAwait(false);
            },
        };
    }

    /// <summary>
    /// An RFC 4918 error response: <c>&lt;D:error&gt;</c> holding the empty precondition element <paramref name="condition"/>,
    /// such as <c>valid-sync-token</c>.
    /// </summary>
    public static DavResponse Error(int statusCode, DavName condition) => Xml(statusCode, (output, _) => {
        var writer = output.Writer;
        DavXml.StartElement(writer, DavNames.D.Error);
        DavXml.DeclarePrefixes(writer);
        DavXml.EmptyElement(writer, condition);
        writer.WriteEndElement();
        return ValueTask.CompletedTask;
    });
}

/// <summary>
/// The forward XML writer of a response body: an <see cref="XmlWriter"/> over a buffer that is copied to the response
/// stream asynchronously whenever it fills, so neither the whole document is held nor a synchronous write reaches the
/// host's stream.
/// </summary>
public sealed class DavXmlOutput : IAsyncDisposable {
    private const int FlushThreshold = 32 * 1024;
    private readonly MemoryStream _buffer = new();
    private readonly Stream _destination;

    /// <summary>Creates the output over <paramref name="destination"/>, which it does not close.</summary>
    public DavXmlOutput(Stream destination) {
        ArgumentNullException.ThrowIfNull(destination);
        _destination = destination;
        Writer = XmlWriter.Create(_buffer, DavXml.WriterSettings);
        Writer.WriteStartDocument();
    }

    /// <summary>The writer; write synchronously, then call <see cref="FlushIfFullAsync"/> between elements.</summary>
    public XmlWriter Writer { get; }

    /// <summary>Copies the buffer to the response stream when it holds enough bytes.</summary>
    public async ValueTask FlushIfFullAsync(CancellationToken cancellationToken) {
        Writer.Flush();
        if (_buffer.Length < FlushThreshold) return;
        await CopyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ends the document and copies the rest of the buffer.</summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken) {
        Writer.WriteEndDocument();
        Writer.Flush();
        await CopyAsync(cancellationToken).ConfigureAwait(false);
        await _destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        Writer.Dispose();
        _buffer.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Writes the <c>D:status</c> element of <paramref name="statusCode"/>, such as <c>HTTP/1.1 404 Not Found</c>.</summary>
    public void WriteStatus(int statusCode) => DavXml.TextElement(Writer, DavNames.D.Status, StatusLine(statusCode));

    /// <summary>The status line of <paramref name="statusCode"/>.</summary>
    public static string StatusLine(int statusCode) =>
        "HTTP/1.1 " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + statusCode switch {
            200 => "OK",
            207 => "Multi-Status",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            412 => "Precondition Failed",
            413 => "Request Entity Too Large",
            500 => "Internal Server Error",
            507 => "Insufficient Storage",
            _ => "Status",
        };

    private async ValueTask CopyAsync(CancellationToken cancellationToken) {
        if (_buffer.Length == 0) return;
        await _destination.WriteAsync(_buffer.GetBuffer().AsMemory(0, (int)_buffer.Length), cancellationToken).ConfigureAwait(false);
        _buffer.SetLength(0);
    }
}
