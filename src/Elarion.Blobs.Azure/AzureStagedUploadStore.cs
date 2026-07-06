using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace Elarion.Blobs.Azure;

/// <summary>
/// Azure-native <see cref="IStagedUploadStore"/>: each session is one append blob in a staging
/// container, so every resumable-upload operation runs on the bare Azure SDK — no relational staging
/// table.
/// </summary>
/// <remarks>
/// <para>
/// The offset guard is server-side atomic: every appended block carries an
/// <c>If-Append-Position-Equal</c> precondition, so a stale or concurrent append fails with <c>412</c>
/// and surfaces as a <see cref="StagedUploadConflictException"/>. The current offset is the append
/// blob's committed length; session state (target container/name, declared length, transport metadata,
/// owner, expiry) lives in the staging blob's metadata.
/// </para>
/// <para>
/// Completion is a <b>server-side copy</b> from the staging append blob to the final location (the
/// co-optimization the seam permits for a provider pair — bytes never round-trip through the
/// application), stamping the pending-lifecycle metadata that <see cref="AzureBlobStore"/> understands.
/// The staging blob is then recreated empty with a completion marker, dropping the staged bytes
/// immediately while the session stays queryable until its retention deadline. Completion is idempotent;
/// a crash between copy and marker is healed by the retry re-running the (overwriting) copy.
/// </para>
/// <para>
/// Limits inherited from append blobs: at most 50,000 blocks per session (appends are split into
/// max-block-size chunks), roughly 195 GiB per upload — beyond the framework's small-to-mid tier.
/// </para>
/// </remarks>
public sealed class AzureStagedUploadStore(
    BlobServiceClient client,
    AzureStagedUploadOptions options,
    ILogger<AzureStagedUploadStore> logger) : IStagedUploadStore {
    private bool _stagingContainerEnsured;

    /// <inheritdoc />
    public async Task<StagedUpload> CreateAsync(StagedUploadCreation creation, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentException.ThrowIfNullOrWhiteSpace(creation.Container, nameof(creation));
        ArgumentException.ThrowIfNullOrWhiteSpace(creation.Name, nameof(creation));
        ArgumentException.ThrowIfNullOrWhiteSpace(creation.ContentType, nameof(creation));

        await EnsureStagingContainerAsync(cancellationToken);

        var id = Guid.CreateVersion7().ToString("N");
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) {
            [AzureBlobMetadata.ContainerKey] = AzureBlobMetadata.Encode(creation.Container),
            [AzureBlobMetadata.NameKey] = AzureBlobMetadata.Encode(creation.Name),
            [AzureBlobMetadata.ExpiresAtKey] = AzureBlobMetadata.FormatInstant(creation.ExpiresAt),
        };
        if (creation.Length is { } declared) {
            metadata[AzureBlobMetadata.LengthKey] = AzureBlobMetadata.FormatLong(declared);
        }

        if (creation.Metadata is { } transportMetadata) {
            metadata[AzureBlobMetadata.TransportMetadataKey] = AzureBlobMetadata.Encode(transportMetadata);
        }

        if (creation.OwnerId is { } ownerId) {
            metadata[AzureBlobMetadata.OwnerKey] = AzureBlobMetadata.Encode(ownerId);
        }

        await StagingBlob(id).CreateAsync(
            new AppendBlobCreateOptions {
                HttpHeaders = new BlobHttpHeaders { ContentType = creation.ContentType },
                Metadata = metadata,
            },
            cancellationToken);

        return new StagedUpload {
            Id = id,
            Length = creation.Length,
            Offset = 0,
            ContentType = creation.ContentType,
            Metadata = creation.Metadata,
            OwnerId = creation.OwnerId,
            ExpiresAt = creation.ExpiresAt,
            BlobRef = null,
        };
    }

    /// <inheritdoc />
    public async Task<StagedUpload?> GetAsync(string uploadId, CancellationToken cancellationToken) {
        try {
            var properties = await StagingBlob(uploadId).GetPropertiesAsync(cancellationToken: cancellationToken);
            return Map(uploadId, properties.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<StagedUpload> AppendAsync(
        string uploadId,
        long offset,
        Stream chunk,
        CancellationToken cancellationToken) {
        var blob = StagingBlob(uploadId);

        BlobProperties properties;
        try {
            properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) {
            throw new StagedUploadConflictException($"Upload session '{uploadId}' does not exist.");
        }

        if (properties.Metadata.ContainsKey(AzureBlobMetadata.BlobRefKey)) {
            throw new StagedUploadConflictException($"Upload session '{uploadId}' is already complete.");
        }

        if (properties.ContentLength != offset) {
            throw new StagedUploadConflictException(
                $"The append offset {offset} does not match the current offset {properties.ContentLength}.");
        }

        // A declared length caps the read to the remaining bytes; a deferred length reads the caller's
        // whole (caller-bounded) chunk. The chunk is split into append blocks, each guarded on the exact
        // append position, so the whole sequence is offset-atomic against concurrent appends.
        var declaredLength = AzureBlobMetadata.ParseLong(properties.Metadata, AzureBlobMetadata.LengthKey);
        var remaining = declaredLength is { } declared ? declared - offset : long.MaxValue;
        var buffer = new byte[blob.AppendBlobMaxAppendBlockBytes];
        long total = 0;
        while (total < remaining) {
            var toRead = (int)Math.Min(buffer.Length, remaining - total);
            var filled = await FillAsync(chunk, buffer.AsMemory(0, toRead), cancellationToken);
            if (filled == 0) {
                break;
            }

            try {
                await blob.AppendBlockAsync(
                    new MemoryStream(buffer, 0, filled, writable: false),
                    new AppendBlobAppendBlockOptions {
                        Conditions = new AppendBlobRequestConditions { IfAppendPositionEqual = offset + total },
                    },
                    cancellationToken);
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 412) {
                throw new StagedUploadConflictException(
                    $"Upload session '{uploadId}' is no longer at offset {offset + total}.");
            }

            total += filled;
        }

        return Map(uploadId, properties) with { Offset = offset + total };
    }

    /// <inheritdoc />
    public async Task<StagedUpload> CompleteAsync(
        string uploadId,
        StagedUploadCompletion completion,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(completion);
        var blob = StagingBlob(uploadId);

        BlobProperties properties;
        try {
            properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) {
            throw new StagedUploadConflictException($"Upload session '{uploadId}' does not exist.");
        }

        if (properties.Metadata.ContainsKey(AzureBlobMetadata.BlobRefKey)) {
            return Map(uploadId, properties);
        }

        var received = properties.ContentLength;
        if (AzureBlobMetadata.ParseLong(properties.Metadata, AzureBlobMetadata.LengthKey) is { } declared
            && received != declared) {
            throw new StagedUploadConflictException(
                $"Upload session '{uploadId}' declares {declared} bytes but received {received}.");
        }

        var container = AzureBlobMetadata.Decode(properties.Metadata, AzureBlobMetadata.ContainerKey)
            ?? throw new InvalidOperationException($"Upload session '{uploadId}' carries no target container.");
        var name = AzureBlobMetadata.Decode(properties.Metadata, AzureBlobMetadata.NameKey)
            ?? throw new InvalidOperationException($"Upload session '{uploadId}' carries no target name.");
        var ownerId = AzureBlobMetadata.Decode(properties.Metadata, AzureBlobMetadata.OwnerKey);
        var location = new AzureBlobLocation(container, name);

        // Server-side copy to the final location, stamping the pending-lifecycle metadata in the same
        // operation (copy carries the source's content type). The copy overwrites, so a completion retry
        // after a crash converges on the same target.
        var targetMetadata = new Dictionary<string, string>(StringComparer.Ordinal) {
            [AzureBlobMetadata.StateKey] = AzureBlobMetadata.PendingState,
        };
        if (completion.BlobExpiresAt is { } blobExpiresAt) {
            targetMetadata[AzureBlobMetadata.ExpiresAtKey] = AzureBlobMetadata.FormatInstant(blobExpiresAt);
        }

        if (ownerId is not null) {
            targetMetadata[AzureBlobMetadata.OwnerKey] = AzureBlobMetadata.Encode(ownerId);
        }

        var targetContainer = client.GetBlobContainerClient(container);
        await targetContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var copy = await targetContainer.GetBlobClient(name).StartCopyFromUriAsync(
            blob.Uri,
            new BlobCopyFromUriOptions { Metadata = targetMetadata },
            cancellationToken);
        await copy.WaitForCompletionAsync(cancellationToken);

        // Recreate the staging blob empty with the completion marker: the staged bytes are dropped
        // immediately (no duplicate storage during the retention window) while the session record stays
        // queryable so a status probe can still fetch the blob reference.
        var completedMetadata = new Dictionary<string, string>(properties.Metadata, StringComparer.Ordinal) {
            [AzureBlobMetadata.LengthKey] = AzureBlobMetadata.FormatLong(received),
            [AzureBlobMetadata.FinalOffsetKey] = AzureBlobMetadata.FormatLong(received),
            [AzureBlobMetadata.BlobRefKey] = AzureBlobMetadata.Encode(location.ToBlobRef().Value),
            [AzureBlobMetadata.ExpiresAtKey] = AzureBlobMetadata.FormatInstant(completion.SessionExpiresAt),
        };
        await blob.CreateAsync(
            new AppendBlobCreateOptions {
                HttpHeaders = new BlobHttpHeaders { ContentType = properties.ContentType },
                Metadata = completedMetadata,
            },
            cancellationToken);

        logger.LogInformation(
            "Staged upload {UploadId} completed into {Container}/{Name} ({Size} bytes).",
            uploadId,
            container,
            name,
            received);

        return new StagedUpload {
            Id = uploadId,
            Length = received,
            Offset = received,
            ContentType = properties.ContentType,
            Metadata = AzureBlobMetadata.Decode(properties.Metadata, AzureBlobMetadata.TransportMetadataKey),
            OwnerId = ownerId,
            ExpiresAt = completion.SessionExpiresAt,
            BlobRef = location.ToBlobRef(),
        };
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string uploadId, CancellationToken cancellationToken) =>
        await StagingBlob(uploadId).DeleteIfExistsAsync(cancellationToken: cancellationToken);

    /// <inheritdoc />
    public async Task<int> DeleteExpiredAsync(DateTimeOffset olderThanUtc, int batchSize, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var container = client.GetBlobContainerClient(options.StagingContainer);
        var deleted = 0;
        try {
            var listing = container.GetBlobsAsync(
                new GetBlobsOptions { Traits = BlobTraits.Metadata }, cancellationToken);
            await foreach (var blobItem in listing) {
                if (blobItem.Metadata is null
                    || AzureBlobMetadata.ParseInstant(blobItem.Metadata, AzureBlobMetadata.ExpiresAtKey)
                        is not { } expiresAt
                    || expiresAt >= olderThanUtc) {
                    continue;
                }

                try {
                    var response = await container.GetBlobClient(blobItem.Name).DeleteIfExistsAsync(
                        conditions: new BlobRequestConditions { IfMatch = blobItem.Properties.ETag },
                        cancellationToken: cancellationToken);
                    if (response.Value) {
                        deleted++;
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 412) {
                    // Touched concurrently (an append or completion moved the expiry); leave it alone.
                }

                if (deleted >= batchSize) {
                    return deleted;
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404) {
            // No staging container yet — nothing was ever staged.
        }

        return deleted;
    }

    private AppendBlobClient StagingBlob(string uploadId) =>
        client.GetBlobContainerClient(options.StagingContainer).GetAppendBlobClient(uploadId);

    private async Task EnsureStagingContainerAsync(CancellationToken cancellationToken) {
        if (_stagingContainerEnsured) {
            return;
        }

        await client.GetBlobContainerClient(options.StagingContainer)
            .CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        _stagingContainerEnsured = true;
    }

    private static StagedUpload Map(string uploadId, BlobProperties properties) {
        var blobRefValue = AzureBlobMetadata.Decode(properties.Metadata, AzureBlobMetadata.BlobRefKey);
        var offset = blobRefValue is null
            ? properties.ContentLength
            : AzureBlobMetadata.ParseLong(properties.Metadata, AzureBlobMetadata.FinalOffsetKey) ?? 0;

        return new StagedUpload {
            Id = uploadId,
            Length = AzureBlobMetadata.ParseLong(properties.Metadata, AzureBlobMetadata.LengthKey),
            Offset = offset,
            ContentType = properties.ContentType,
            Metadata = AzureBlobMetadata.Decode(properties.Metadata, AzureBlobMetadata.TransportMetadataKey),
            OwnerId = AzureBlobMetadata.Decode(properties.Metadata, AzureBlobMetadata.OwnerKey),
            ExpiresAt = AzureBlobMetadata.ParseInstant(properties.Metadata, AzureBlobMetadata.ExpiresAtKey)
                ?? DateTimeOffset.MinValue,
            BlobRef = blobRefValue is null ? null : new BlobRef { Value = blobRefValue },
        };
    }

    private static async Task<int> FillAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken) {
        var total = 0;
        while (total < destination.Length) {
            var read = await source.ReadAsync(destination[total..], cancellationToken);
            if (read == 0) {
                break;
            }

            total += read;
        }

        return total;
    }
}
