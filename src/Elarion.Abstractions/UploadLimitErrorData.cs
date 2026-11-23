namespace Elarion.Abstractions;

/// <summary>
/// The payload of <see cref="ErrorCodes.UploadTooLarge"/>: the limit the file broke and its size, so a client words the
/// answer in its own language and unit instead of showing the server's English message.
/// </summary>
/// <remarks>
/// A handler whose request declares <see cref="UploadLimitsAttribute.MaxBytes"/> has the code with this payload in its
/// error contract without declaring it; one that passes <see cref="UploadConstraints.MaxBytes"/> at read time declares
/// <c>[ProducesError(ErrorCodes.UploadTooLarge, ErrorKind.Validation, typeof(UploadTooLargeData))]</c>.
/// </remarks>
public sealed record UploadTooLargeData {
    /// <summary>The largest accepted size in bytes.</summary>
    public required long MaxBytes { get; init; }

    /// <summary>The size of the uploaded file in bytes.</summary>
    public required long Length { get; init; }
}

/// <summary>
/// The payload of <see cref="ErrorCodes.UploadContentType"/>: the accepted media types and the one the file was
/// uploaded with, so a client words the answer itself.
/// </summary>
/// <remarks>
/// Implied for a request that declares <see cref="UploadLimitsAttribute.ContentTypes"/>, as
/// <see cref="UploadTooLargeData"/> is for a size limit.
/// </remarks>
public sealed record UploadContentTypeData {
    /// <summary>The accepted media types, as declared.</summary>
    public required IReadOnlyList<string> ContentTypes { get; init; }

    /// <summary>The media type the client declared for the file, without parameters.</summary>
    public required string ContentType { get; init; }
}

/// <summary>
/// The payload of <see cref="ErrorCodes.UploadFileExtension"/>: the accepted file-name extensions and the name the file
/// was stored with, so a client words the answer itself.
/// </summary>
/// <remarks>
/// Implied for a request that declares <see cref="UploadLimitsAttribute.FileExtensions"/>, as
/// <see cref="UploadTooLargeData"/> is for a size limit.
/// </remarks>
public sealed record UploadFileExtensionData {
    /// <summary>The accepted extensions, as declared (for example <c>".xlsx"</c>).</summary>
    public required IReadOnlyList<string> FileExtensions { get; init; }

    /// <summary>The file name the upload endpoint stored (<c>upload</c> when the client sent none).</summary>
    public required string FileName { get; init; }
}
