namespace Elarion.Abstractions;

/// <summary>
/// The limits a handler accepts for an <see cref="UploadedFile"/>, checked by <see cref="IUploadedFiles"/> against
/// what the upload endpoint recorded. The endpoints enforce their own, host-wide limits while the bytes arrive; these
/// are the handler's, per operation.
/// </summary>
public sealed record UploadConstraints {
    /// <summary>
    /// The largest accepted size in bytes, or <see langword="null"/> for no limit. A larger upload answers
    /// <see cref="ErrorCodes.UploadTooLarge"/>.
    /// </summary>
    public long? MaxBytes { get; init; }

    /// <summary>
    /// The accepted media types (for example <c>"text/csv"</c>), or <see langword="null"/> or empty to accept any.
    /// Compared exactly and case-insensitively, with parameters such as <c>; charset=utf-8</c> ignored on both sides.
    /// Another type answers <see cref="ErrorCodes.UploadContentType"/>.
    /// </summary>
    /// <remarks>
    /// The content type is the one the client declared when it uploaded, not sniffed from the bytes: it keeps a client
    /// to the operation's contract, it does not prove what the file contains. A parser still treats the content as
    /// untrusted input.
    /// </remarks>
    public IReadOnlyList<string>? ContentTypes { get; init; }
}
