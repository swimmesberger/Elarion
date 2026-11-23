namespace Elarion.Abstractions;

/// <summary>
/// The limits a handler accepts for an <see cref="UploadedFile"/>, checked by <see cref="IUploadedFiles"/> against
/// what the upload endpoint recorded. The endpoints enforce their own, host-wide limits while the bytes arrive; these
/// are the handler's, per operation. Every limit set must hold.
/// </summary>
public sealed record UploadConstraints {
    /// <summary>
    /// The largest accepted size in bytes, or <see langword="null"/> for no limit. A larger upload answers
    /// <see cref="ErrorCodes.UploadTooLarge"/> with an <see cref="UploadTooLargeData"/>.
    /// </summary>
    public long? MaxBytes { get; init; }

    /// <summary>
    /// The accepted media types (for example <c>"text/csv"</c>), or <see langword="null"/> or empty to accept any.
    /// Compared exactly and case-insensitively, with parameters such as <c>; charset=utf-8</c> ignored on both sides.
    /// Another type answers <see cref="ErrorCodes.UploadContentType"/> with an <see cref="UploadContentTypeData"/>.
    /// </summary>
    /// <remarks>
    /// The content type is the one the client declared when it uploaded, not sniffed from the bytes: it keeps a client
    /// to the operation's contract, it does not prove what the file contains. A parser still treats the content as
    /// untrusted input. Browsers declare some formats differently from system to system, or not at all (sent as
    /// <c>application/octet-stream</c>): for those, accept <see cref="FileExtensions"/> instead.
    /// </remarks>
    public IReadOnlyList<string>? ContentTypes { get; init; }

    /// <summary>
    /// The accepted file-name extensions, with the leading dot (for example <c>".xlsx"</c>), or <see langword="null"/> or
    /// empty to accept any. The file name the upload endpoint stored must end in one of them, compared
    /// case-insensitively; an extension given without the dot is compared as if it had one. Another name answers
    /// <see cref="ErrorCodes.UploadFileExtension"/> with an <see cref="UploadFileExtensionData"/>.
    /// </summary>
    /// <remarks>
    /// Like the content type, the name is the client's claim, not a property of the bytes. Prefer it to
    /// <see cref="ContentTypes"/> for formats whose declared type varies between systems, such as <c>.xlsx</c>,
    /// <c>.vcf</c> and <c>.ics</c>. Declaring both requires both.
    /// </remarks>
    public IReadOnlyList<string>? FileExtensions { get; init; }
}
