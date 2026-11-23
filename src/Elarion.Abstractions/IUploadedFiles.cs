namespace Elarion.Abstractions;

/// <summary>
/// Reads, keeps and discards the files a caller staged through the upload endpoints, for a handler that takes an
/// <see cref="UploadedFile"/>. It is the only safe way from the reference to the bytes: every operation answers only
/// for a <b>pending</b> upload of the <b>current user</b> in an <b>upload container</b>, and fails closed otherwise.
/// </summary>
/// <remarks>
/// <para>
/// The checks, all of which must hold: the blob exists; it is still pending (a kept blob belongs to a record and that
/// record's authorization, so it is not an upload any more); its recorded owner is exactly the current user's id (an
/// unauthenticated caller and an unowned blob never match); and it lies in one of the configured upload containers
/// (by default <c>uploads</c>, the container of both upload endpoints). A failed check answers
/// <see cref="ErrorCodes.UploadNotFound"/>, the same error for every cause, so a caller cannot probe which ids exist.
/// The <see cref="UploadConstraints"/> are checked after them, in order: <see cref="ErrorCodes.UploadTooLarge"/>,
/// <see cref="ErrorCodes.UploadContentType"/> and <see cref="ErrorCodes.UploadFileExtension"/>, each carrying the limit
/// as typed data (<see cref="UploadTooLargeData"/>, <see cref="UploadContentTypeData"/>,
/// <see cref="UploadFileExtensionData"/>). All four are <see cref="ErrorKind.Validation"/> errors the handler returns. A
/// request that declares <see cref="UploadLimitsAttribute"/> has them in its error contract already; a handler that only
/// passes constraints declares the ones it can produce with <see cref="ProducesErrorAttribute"/>, with their payload
/// type.
/// </para>
/// <para>
/// An upload stays pending, and readable, until <see cref="CommitAsync"/> keeps it, <see cref="DeleteAsync"/>
/// discards it, or the blob garbage collector removes it after the upload endpoint's <c>Ttl</c> (30 minutes by
/// default). Raise that <c>Ttl</c> when a multi-step flow needs the file for longer.
/// </para>
/// <para>
/// The implementation is registered by <c>AddElarionUploadedFiles</c> in <c>Elarion.Blobs</c>, over the registered
/// <c>IBlobStore</c> and <c>ICurrentUser</c>.
/// </para>
/// </remarks>
public interface IUploadedFiles {
    /// <summary>
    /// Opens the upload for reading. The caller disposes the result once it has read the content.
    /// </summary>
    /// <param name="file">The reference the request carried.</param>
    /// <param name="constraints">The size, content-type and file-extension limits the handler accepts; <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// The open content, or <see cref="ErrorCodes.UploadNotFound"/>, <see cref="ErrorCodes.UploadTooLarge"/>,
    /// <see cref="ErrorCodes.UploadContentType"/> or <see cref="ErrorCodes.UploadFileExtension"/>. The upload stays
    /// pending: open it again in a later step, keep it with <see cref="CommitAsync"/>, or let it expire.
    /// </returns>
    ValueTask<Result<UploadedFileContent>> OpenAsync(
        UploadedFile file,
        UploadConstraints? constraints = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps the upload: after the same checks as <see cref="OpenAsync"/>, promotes the pending blob to committed so
    /// the garbage collector never removes it, and returns the reference the application stores on its record.
    /// </summary>
    /// <param name="file">The reference the request carried.</param>
    /// <param name="constraints">The size, content-type and file-extension limits the handler accepts; <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The kept file's blob id and description, or the same errors as <see cref="OpenAsync"/>.</returns>
    /// <remarks>
    /// The commit joins the caller's unit of work where the blob store supports it (the PostgreSQL store does): call
    /// it inside the transaction that saves the referencing record, so both commit together, and a rollback leaves
    /// the upload pending for the garbage collector. Once kept, the blob is no longer an upload: every operation of
    /// this service answers <see cref="ErrorCodes.UploadNotFound"/> for it, and the application reads it through its
    /// record and the blob store.
    /// </remarks>
    ValueTask<Result<CommittedUpload>> CommitAsync(
        UploadedFile file,
        UploadConstraints? constraints = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the upload now instead of waiting for the garbage collector, for a flow that consumed it.
    /// </summary>
    /// <param name="file">The reference the request carried.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> when this call deleted the upload; <see langword="false"/> when there was no pending
    /// upload of the caller to delete (already deleted, expired, kept, or someone else's), which is not an error, so
    /// the call is idempotent.
    /// </returns>
    /// <remarks>
    /// The delete is the blob store's. The PostgreSQL store removes the row through the scoped <c>DbContext</c> it
    /// shares with the handler and saves that context, so the call also writes every change the handler still tracks
    /// at that moment: call it after the handler's own <c>SaveChangesAsync</c>, never between changing tracked entities
    /// and saving them. Inside the handler's transaction the delete commits and rolls back with it. On a store outside
    /// the database (Azure Blob Storage) it is an immediate side effect: delete only after the transaction commits, or
    /// leave the upload to the garbage collector.
    /// </remarks>
    ValueTask<bool> DeleteAsync(UploadedFile file, CancellationToken cancellationToken = default);
}
