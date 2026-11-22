namespace Elarion.Abstractions;

/// <summary>
/// Declares the size, content-type and file-extension limits of an <see cref="UploadedFile"/> request member, once, where both sides
/// read them: the handler pipeline applies them to every read and keep of the file through <see cref="IUploadedFiles"/>
/// (the same check as passing <see cref="UploadConstraints"/> to it), and the exported schema carries them, so the
/// generated TypeScript client rejects a file that breaks them before uploading it.
/// </summary>
/// <remarks>
/// <para>
/// Put it on a member of the handler's request type whose type is <see cref="UploadedFile"/>, a nullable
/// <see cref="UploadedFile"/>, or a collection of them (an array, a list, a read-only collection); on a collection the
/// limits apply to every file, and the collection's own <c>[MaxLength]</c> bounds how many there are. Any other placement
/// is a build error (<c>ELUPL001</c>, <c>ELUPL002</c>): a declaration the server would not enforce is never accepted.
/// </para>
/// <para>
/// The pipeline declares the limits for the files of the current request before the handler runs (fail closed: the
/// handler needs <c>AddElarionUploadedFiles</c>, whose <see cref="IUploadedFiles"/> applies them). A file that breaks
/// them answers <see cref="ErrorCodes.UploadTooLarge"/>, <see cref="ErrorCodes.UploadContentType"/> or
/// <see cref="ErrorCodes.UploadFileExtension"/>, each with the limit as typed data (<see cref="UploadTooLargeData"/>,
/// <see cref="UploadContentTypeData"/>, <see cref="UploadFileExtensionData"/>) so a client words it in its own language.
/// The declaration puts those codes, with their payloads, and <see cref="ErrorCodes.UploadNotFound"/> into the
/// handler's error contract: the handler declares none of them itself. Constraints the handler still passes to
/// <see cref="IUploadedFiles"/> apply on top: every declared and every passed limit must hold.
/// </para>
/// <para>
/// The schema exports the limits on the member as <c>x-elarion-max-bytes</c>, <c>x-elarion-content-types</c> and
/// <c>x-elarion-file-extensions</c> next to <c>x-elarion-upload</c>. The client's check is a convenience; the server's
/// is the authority. On a positional record, target the property (<c>[property: UploadLimits(…)]</c>); a parameter
/// target is accepted too.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed record Command : ICommand {
///     [UploadLimits(MaxBytes = 50L * 1024 * 1024, ContentTypes = ["text/csv", "application/xml"])]
///     public required UploadedFile Statement { get; init; }
///
///     // A spreadsheet's declared type varies between systems: accept it by its extension.
///     [UploadLimits(MaxBytes = 10L * 1024 * 1024, FileExtensions = [".xlsx", ".xls"])]
///     public UploadedFile? Workbook { get; init; }
///
///     [MaxLength(10), UploadLimits(MaxBytes = 5L * 1024 * 1024, ContentTypes = ["application/pdf"])]
///     public IReadOnlyList&lt;UploadedFile&gt; Receipts { get; init; } = [];
/// }
///
/// // In the handler: the declared limits apply without being repeated.
/// var opened = await uploads.OpenAsync(command.Statement, cancellationToken: ct);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class UploadLimitsAttribute : Attribute {
    /// <summary>
    /// The largest accepted size in bytes; <c>0</c> (the default) for no limit. A larger file answers
    /// <see cref="ErrorCodes.UploadTooLarge"/>.
    /// </summary>
    public long MaxBytes { get; set; }

    /// <summary>
    /// The accepted media types (for example <c>"text/csv"</c>); <see langword="null"/> or empty to accept any. Compared
    /// as <see cref="UploadConstraints.ContentTypes"/> compares them. Another type answers
    /// <see cref="ErrorCodes.UploadContentType"/>.
    /// </summary>
    public string[]? ContentTypes { get; set; }

    /// <summary>
    /// The accepted file-name extensions, each with its leading dot (for example <c>".xlsx"</c>); <see langword="null"/>
    /// or empty to accept any. Compared as <see cref="UploadConstraints.FileExtensions"/> compares them. Another name
    /// answers <see cref="ErrorCodes.UploadFileExtension"/>. Use it instead of <see cref="ContentTypes"/> for formats
    /// whose declared type varies between systems (<c>.xlsx</c>, <c>.vcf</c>, <c>.ics</c>); declaring both requires both.
    /// An extension without the dot, or with a path separator or white space, is a build error (<c>ELUPL001</c>).
    /// </summary>
    public string[]? FileExtensions { get; set; }

    /// <summary>The declared limits as the <see cref="UploadConstraints"/> that <see cref="IUploadedFiles"/> checks.</summary>
    public UploadConstraints ToConstraints() {
        return new UploadConstraints {
            MaxBytes = MaxBytes > 0 ? MaxBytes : null,
            ContentTypes = ContentTypes is { Length: > 0 } types ? [.. types] : null,
            FileExtensions = FileExtensions is { Length: > 0 } extensions ? [.. extensions] : null
        };
    }
}
