using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Microsoft.Extensions.Logging;

namespace Elarion.Blobs;

/// <summary>
/// The <see cref="IUploadedFiles"/> over the registered <see cref="IBlobStore"/>: an upload is a pending blob in an
/// upload container whose recorded owner is exactly the current user, and anything else is answered as missing.
/// </summary>
/// <remarks>
/// Ownership is compared against <see cref="BlobMetadata.OwnerId"/>, never parsed from the stored name, like the upload
/// endpoints' own cancel and download checks. The reason a reference was refused is logged at debug level only; the
/// caller sees one error for every cause.
/// </remarks>
internal sealed class UploadedFiles(
    IBlobStore blobStore,
    ICurrentUser currentUser,
    UploadedFilesOptions options,
    ILogger<UploadedFiles> logger,
    IBlobLifecycle? lifecycle = null) : IUploadedFiles {
    // Longer than any id a store mints; a longer value is not an id, and is refused before it reaches the store.
    private const int MaxIdLength = 1024;

    private static readonly AppError NotFound = AppError.Validation(
        "The uploaded file was not found or has expired. Upload it again.", ErrorCodes.UploadNotFound);

    public async ValueTask<Result<UploadedFileContent>> OpenAsync(
        UploadedFile file,
        UploadConstraints? constraints = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(file);

        if (!TryGetBlobRef(file, out var blobRef)) return NotFound;

        // One store call for metadata and content, so the checked metadata is the content's own.
        var download = await blobStore.OpenReadAsync(blobRef, cancellationToken).ConfigureAwait(false);
        if (download is null) return Refused(blobRef, "it does not exist");

        var metadata = download.Metadata;
        if (Check(blobRef, metadata, constraints) is { } error) {
            await download.DisposeAsync().ConfigureAwait(false);
            return error;
        }

        return new UploadedFileContent(
            download.Content, metadata.ContentType, FileNameOf(metadata.Name), metadata.Size, download) {
            ContentSha256 = metadata.ContentSha256
        };
    }

    public async ValueTask<Result<CommittedUpload>> CommitAsync(
        UploadedFile file,
        UploadConstraints? constraints = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(file);

        if (lifecycle is null)
            throw new InvalidOperationException(
                "Keeping an uploaded file needs a blob store with a pending/committed lifecycle (IBlobLifecycle), "
                + "for example AddElarionPostgreSqlBlobLifecycle or AddElarionAzureBlobLifecycle.");

        if (!TryGetBlobRef(file, out var blobRef)) return NotFound;

        var metadata = await blobStore.GetMetadataAsync(blobRef, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return Refused(blobRef, "it does not exist");

        if (Check(blobRef, metadata, constraints) is { } error) return error;

        // Joins the caller's unit of work where the store supports it; false means the collector removed the blob
        // between the check and the commit.
        if (!await lifecycle.CommitAsync(blobRef, cancellationToken).ConfigureAwait(false))
            return Refused(blobRef, "it was removed before it could be kept");

        return new CommittedUpload {
            BlobId = blobRef.Value,
            FileName = FileNameOf(metadata.Name),
            ContentType = metadata.ContentType,
            Length = metadata.Size,
            ContentSha256 = metadata.ContentSha256
        };
    }

    public async ValueTask<bool> DeleteAsync(UploadedFile file, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(file);

        if (!TryGetBlobRef(file, out var blobRef)) return false;

        var metadata = await blobStore.GetMetadataAsync(blobRef, cancellationToken).ConfigureAwait(false);
        if (metadata is null || Mismatch(metadata) is not null) return false;

        return await blobStore.DeleteAsync(blobRef, cancellationToken).ConfigureAwait(false);
    }

    private bool TryGetBlobRef(UploadedFile file, out BlobRef blobRef) {
        blobRef = default;

        // An anonymous caller owns no upload; refuse before the store is asked anything.
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.UserId)) return false;

        var id = file.Id;
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength || id.Any(char.IsControl)) return false;

        blobRef = new BlobRef { Value = id };
        return true;
    }

    private AppError? Check(BlobRef blobRef, BlobMetadata metadata, UploadConstraints? constraints) {
        if (Mismatch(metadata) is { } reason) return Refused(blobRef, reason);

        if (constraints is null) return null;

        if (constraints.MaxBytes is { } maxBytes && metadata.Size > maxBytes)
            return AppError.Validation(
                $"The file is larger than the accepted {maxBytes} bytes.", ErrorCodes.UploadTooLarge);

        if (constraints.ContentTypes is { Count: > 0 } contentTypes && !IsAccepted(metadata.ContentType, contentTypes))
            return AppError.Validation(
                $"Files of type '{MediaType(metadata.ContentType)}' are not accepted here; expected "
                + $"{string.Join(", ", contentTypes)}.",
                ErrorCodes.UploadContentType);

        return null;
    }

    private string? Mismatch(BlobMetadata metadata) {
        if (metadata.State != BlobLifecycleState.Pending) return "it is no longer pending";

        if (!options.Containers.Contains(metadata.Container, StringComparer.Ordinal))
            return $"its container '{metadata.Container}' is not an upload container";

        // Exact comparison with the recorded owner; an unowned blob is nobody's upload (fail closed).
        if (metadata.OwnerId is null || !string.Equals(metadata.OwnerId, currentUser.UserId, StringComparison.Ordinal))
            return "it belongs to another owner";

        return null;
    }

    private AppError Refused(BlobRef blobRef, string reason) {
        logger.LogDebug("Uploaded file {BlobId} is answered as not found: {Reason}.", blobRef.Value, reason);
        return NotFound;
    }

    private static bool IsAccepted(string contentType, IReadOnlyList<string> accepted) {
        var actual = MediaType(contentType);
        foreach (var candidate in accepted)
            if (MediaType(candidate).Equals(actual, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static string MediaType(string contentType) {
        var semicolon = contentType.IndexOf(';');
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
    }

    // The upload endpoints store "{owner}/{guid}/{fileName}" with a separator-free, sanitized file name.
    private static string FileNameOf(string name) {
        var leaf = name[(name.LastIndexOf('/') + 1)..];
        return leaf.Length > 0 ? leaf : "upload";
    }
}
