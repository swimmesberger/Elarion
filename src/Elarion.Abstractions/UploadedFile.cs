using System.Text.Json.Serialization;
using Elarion.Abstractions.Serialization;

namespace Elarion.Abstractions;

/// <summary>
/// A handler parameter that names a file the caller staged through the upload endpoints (the direct upload or tus):
/// the large-file counterpart of <see cref="ElarionFile"/>. The bytes travel once, streamed, over the upload endpoint;
/// the handler request carries only this reference. On every JSON surface it is a plain string, the blob id the upload
/// endpoint answered with, and the exported schema marks it <c>x-elarion-upload</c> so the generated TypeScript
/// client accepts a <c>File</c> and uploads it before the call.
/// </summary>
/// <remarks>
/// <para>
/// A reference is not a grant. Read and keep the file only through <see cref="IUploadedFiles"/>, which answers the
/// file only to the user who uploaded it, only while it is still a pending upload, and only from an upload
/// container; anything else answers the same <see cref="ErrorCodes.UploadNotFound"/> error, so a guessed or leaked
/// id reveals nothing. Never pass <see cref="Id"/> to the blob store yourself.
/// </para>
/// <para>
/// An upload stays pending until <see cref="IUploadedFiles.CommitAsync"/> keeps it or the blob garbage collector
/// removes it (the upload endpoints' <c>Ttl</c>, 30 minutes by default), so a multi-step flow (analyze, preview,
/// commit) uploads once and passes the same reference to every step. See ADR-0105.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Handler]
/// [ProducesError(ErrorCodes.UploadNotFound, ErrorKind.Validation)]
/// [ProducesError(ErrorCodes.UploadTooLarge, ErrorKind.Validation, typeof(UploadTooLargeData))]
/// [ProducesError(ErrorCodes.UploadContentType, ErrorKind.Validation, typeof(UploadContentTypeData))]
/// public sealed class AnalyzeStatement(IUploadedFiles uploads, StatementParser parser)
///     : IHandler&lt;AnalyzeStatement.Command, Result&lt;Analysis&gt;&gt; {
///     public sealed record Command : ICommand {
///         public required UploadedFile File { get; init; }
///     }
///
///     public async ValueTask&lt;Result&lt;Analysis&gt;&gt; HandleAsync(Command command, CancellationToken ct) {
///         var opened = await uploads.OpenAsync(command.File, new UploadConstraints {
///             MaxBytes = 50L * 1024 * 1024,
///             ContentTypes = ["text/csv", "application/xml"],
///         }, ct);
///         if (!opened.IsSuccess) return opened.Error;
///
///         await using var file = opened.Value;
///         return await parser.AnalyzeAsync(file.Content, ct);
///     }
/// }
/// </code>
/// </example>
[JsonConverter(typeof(UploadedFileJsonConverter))]
public sealed record UploadedFile {
    /// <summary>
    /// The blob id the upload endpoint answered with: the direct upload's <c>text/plain</c> body, or the tus
    /// <c>Elarion-Blob-Ref</c> header.
    /// </summary>
    public required string Id { get; init; }
}
