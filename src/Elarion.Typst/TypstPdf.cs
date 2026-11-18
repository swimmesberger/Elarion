using Elarion.Abstractions;

namespace Elarion.Typst;

/// <summary>
/// A compiled PDF, backed by a temporary file (CLI engine) or native memory (in-process engine) that is released when
/// this instance is disposed. Stream it into a blob store or a response without materializing it;
/// <see cref="ToElarionFileAsync"/> buffers it for the small-file tier.
/// </summary>
/// <example>
/// <code>
/// await using var pdf = await typst.CompilePdfAsync(request, cancellationToken);
/// var blob = await blobs.SaveAsync(new BlobUploadRequest {
///     Container = "invoices", Name = $"{invoice.Number}.pdf", ContentType = TypstPdf.ContentType,
/// }, pdf.Content, cancellationToken);
/// </code>
/// </example>
public sealed class TypstPdf : IAsyncDisposable, IDisposable {
    /// <summary>The PDF media type.</summary>
    public const string ContentType = "application/pdf";

    private readonly Stream _content;

    internal TypstPdf(Stream content, string? warnings) {
        _content = content;
        Warnings = string.IsNullOrWhiteSpace(warnings) ? null : warnings;
    }

    /// <summary>The PDF, read-only and positioned at its start.</summary>
    public Stream Content => _content;

    /// <summary>The PDF's size in bytes.</summary>
    public long Length => _content.Length;

    /// <summary>
    /// What Typst reported on standard error for a successful compilation (warnings such as an unknown font family),
    /// or <see langword="null"/>. They are also logged.
    /// </summary>
    public string? Warnings { get; }

    /// <summary>
    /// Reads the PDF into an <see cref="ElarionFile"/> for a handler response (the small-file tier). The content is
    /// read from the current position of <see cref="Content"/>; use a blob for documents of several megabytes.
    /// </summary>
    /// <param name="fileName">The download's file name, such as <c>invoice-2026-0042.pdf</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<ElarionFile> ToElarionFileAsync(string? fileName = null, CancellationToken cancellationToken = default) {
        var buffer = new byte[_content.Length - _content.Position];
        await _content.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new ElarionFile(buffer, ContentType) { FileName = fileName };
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _content.DisposeAsync();

    /// <inheritdoc />
    public void Dispose() => _content.Dispose();
}
