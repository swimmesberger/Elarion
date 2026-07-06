using System.Text.Json.Serialization;
using Elarion.Abstractions.Serialization;

namespace Elarion.Abstractions;

/// <summary>
/// An in-memory binary file payload for handler requests and responses: the content bytes, the MIME content
/// type, and an optional file name. Declaring it says "this handler receives/returns a file" once — every
/// transport then carries it the way that suits it best: the generated <c>[HttpEndpoint]</c> mapping turns a
/// <c>Result&lt;ElarionFile&gt;</c> response into a real file download
/// (<c>Elarion.AspNetCore.ElarionHttpResults.ToFileResult</c>), and the JSON surfaces (JSON-RPC params and
/// results, MCP tool results, HTTP JSON bodies) carry the canonical base64 envelope of
/// <see cref="ElarionFileJsonConverter"/> — both directions, so an <see cref="ElarionFile"/> request property
/// is an upload. The exported schema marks it as a file, so the generated TypeScript client maps it to a
/// native <c>File</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately <b>the small-file tier</b> — a pointer to a block of memory, buffered end to end
/// (base64 adds ~33% on JSON surfaces). It is the right default up to a few megabytes (rule of thumb: 4 MB).
/// Above that, use the <b>staged-blob tier</b>: upload through the provided blob endpoints into the pending
/// (staging) area and hand the handler a blob pointer to stream from — and for large exports, write a pending
/// blob and return its pointer for a streamed download. Pending blobs expire through the blob garbage
/// collector, giving temp-file semantics for free. See the file-handling docs and ADR-0038.
/// </para>
/// <example>
/// <code>
/// // Download (any transport; HTTP streams it as a file response):
/// return new ElarionFile(xlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet") {
///     FileName = "clients.xlsx",
/// };
///
/// // Upload (any transport; the client sends the base64 envelope):
/// public sealed record Command : ICommand {
///     public required string Container { get; init; }
///     public required ElarionFile File { get; init; }
/// }
/// </code>
/// </example>
/// </remarks>
[JsonConverter(typeof(ElarionFileJsonConverter))]
public sealed class ElarionFile {
    /// <summary>Creates a file payload from <paramref name="content"/>.</summary>
    /// <param name="content">The file content. A <c>byte[]</c> converts implicitly.</param>
    /// <param name="contentType">The MIME content type (e.g. <c>"application/pdf"</c>).</param>
    public ElarionFile(ReadOnlyMemory<byte> content, string contentType) {
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        Bytes = content;
        ContentType = contentType;
    }

    /// <summary>The file content.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>The MIME content type.</summary>
    public string ContentType { get; }

    /// <summary>
    /// The file name. On an HTTP download it is sent as a <c>Content-Disposition: attachment</c> header;
    /// leave <c>null</c> for inline content (e.g. an image rendered by the browser).
    /// </summary>
    public string? FileName { get; init; }
}
