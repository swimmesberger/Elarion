namespace Elarion.Abstractions;

/// <summary>
/// An <see cref="UploadedFile"/> that <see cref="IUploadedFiles.CommitAsync"/> kept: the blob id the application
/// stores on its record, with what the upload endpoint recorded about the file.
/// </summary>
public sealed record CommittedUpload {
    /// <summary>
    /// The id of the kept blob. It no longer expires; the application stores it on the record that owns the file and
    /// reads the file through that record and the blob store.
    /// </summary>
    public required string BlobId { get; init; }

    /// <summary>The file name the client sent, as the upload endpoint stored it. Untrusted: never use it as a path.</summary>
    public required string FileName { get; init; }

    /// <summary>The media type the client declared when it uploaded.</summary>
    public required string ContentType { get; init; }

    /// <summary>The content length in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>
    /// The SHA-256 of the content as 64 lower-case hex characters, or <see langword="null"/> when the store recorded
    /// none (ADR-0099).
    /// </summary>
    public string? ContentSha256 { get; init; }
}
