namespace Elarion.Abstractions;

/// <summary>
/// Declares the size and content-type limits of an <see cref="UploadedFile"/> request member, once, where both sides
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
/// them answers <see cref="ErrorCodes.UploadTooLarge"/> or <see cref="ErrorCodes.UploadContentType"/>, which the
/// handler declares with <see cref="ProducesErrorAttribute"/>. Constraints the handler still passes to
/// <see cref="IUploadedFiles"/> apply on top: every declared and every passed limit must hold.
/// </para>
/// <para>
/// The schema exports the limits on the member as <c>x-elarion-max-bytes</c> and <c>x-elarion-content-types</c> next to
/// <c>x-elarion-upload</c>. The client's check is a convenience; the server's is the authority. On a positional record,
/// target the property (<c>[property: UploadLimits(…)]</c>); a parameter target is accepted too.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed record Command : ICommand {
///     [UploadLimits(MaxBytes = 50L * 1024 * 1024, ContentTypes = ["text/csv", "application/xml"])]
///     public required UploadedFile Statement { get; init; }
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

    /// <summary>The declared limits as the <see cref="UploadConstraints"/> that <see cref="IUploadedFiles"/> checks.</summary>
    public UploadConstraints ToConstraints() {
        return new UploadConstraints {
            MaxBytes = MaxBytes > 0 ? MaxBytes : null,
            ContentTypes = ContentTypes is { Length: > 0 } types ? [.. types] : null
        };
    }
}
