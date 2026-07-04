using System.Collections.Concurrent;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace Elarion.Blobs.Azure;

/// <summary>
/// Azure Blob Storage <see cref="IBlobStore"/> + <see cref="IBlobLifecycle"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// A blob reference is <c>{container}/{name}</c>, so an upsert of the same container/name overwrites in
/// place and keeps its reference — matching the PostgreSQL store's semantics. Containers are created on
/// demand and must satisfy Azure container naming rules (the framework applies no transformation).
/// </para>
/// <para>
/// The pending/committed lifecycle lives in blob metadata (<c>elarion_state</c>, <c>elarion_expires_at</c>).
/// Two documented deltas from the transactional PostgreSQL store: <see cref="CommitAsync"/> takes effect
/// immediately (Azure has no ambient transaction to join — if the caller's surrounding transaction rolls
/// back, the blob stays committed), and the commit-versus-collection race is closed with ETag
/// preconditions instead of row locks. <see cref="DeleteExpiredPendingAsync"/> scans container listings
/// with metadata, which fits the framework's 1–10 node tier; at larger scale replace the collector with
/// Azure lifecycle-management policies.
/// </para>
/// </remarks>
public sealed class AzureBlobStore(
    BlobServiceClient client,
    ILogger<AzureBlobStore> logger) : IBlobStore, IBlobLifecycle {
    private readonly ConcurrentDictionary<string, byte> _ensuredContainers = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<BlobRef> SaveAsync(
        BlobUploadRequest request,
        Stream content,
        CancellationToken cancellationToken) {
        ValidateUploadRequest(request);
        ArgumentNullException.ThrowIfNull(content);

        var container = await EnsureContainerAsync(request.Container, cancellationToken);
        var blob = container.GetBlobClient(request.Name);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) {
            [AzureBlobMetadata.StateKey] = request.InitialState == BlobLifecycleState.Pending
                ? AzureBlobMetadata.PendingState
                : AzureBlobMetadata.CommittedState,
        };
        // Expiry is only retained for pending blobs so a committed blob is never accidentally reclaimed.
        if (request.InitialState == BlobLifecycleState.Pending && request.ExpiresAt is { } expiresAt) {
            metadata[AzureBlobMetadata.ExpiresAtKey] = AzureBlobMetadata.FormatInstant(expiresAt);
        }

        if (request.OwnerId is { } ownerId) {
            metadata[AzureBlobMetadata.OwnerKey] = AzureBlobMetadata.Encode(ownerId);
        }

        await blob.UploadAsync(
            content,
            new BlobUploadOptions {
                HttpHeaders = new BlobHttpHeaders { ContentType = request.ContentType },
                Metadata = metadata,
            },
            cancellationToken);

        var location = new AzureBlobLocation(request.Container, request.Name);
        logger.LogInformation(
            "Blob stored: {Container}/{Name} ({ContentType})",
            request.Container,
            request.Name,
            request.ContentType);

        return location.ToBlobRef();
    }

    /// <inheritdoc />
    public async Task<BlobDownload?> OpenReadAsync(BlobRef blobRef, CancellationToken cancellationToken) {
        var location = AzureBlobLocation.Parse(blobRef);
        var blob = client.GetBlobContainerClient(location.Container).GetBlobClient(location.Name);

        try {
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            var content = await blob.OpenReadAsync(
                new BlobOpenReadOptions(allowModifications: false), cancellationToken);
            return new BlobDownload(ToMetadata(location, properties.Value), content);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<BlobMetadata?> GetMetadataAsync(BlobRef blobRef, CancellationToken cancellationToken) {
        var location = AzureBlobLocation.Parse(blobRef);
        var blob = client.GetBlobContainerClient(location.Container).GetBlobClient(location.Name);

        try {
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            return ToMetadata(location, properties.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(BlobRef blobRef, CancellationToken cancellationToken) {
        var location = AzureBlobLocation.Parse(blobRef);
        var blob = client.GetBlobContainerClient(location.Container).GetBlobClient(location.Name);

        var response = await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken);
        if (response.Value) {
            logger.LogInformation("Blob deleted: {BlobRef}", blobRef.Value);
        }

        return response.Value;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(BlobRef blobRef, CancellationToken cancellationToken) {
        var location = AzureBlobLocation.Parse(blobRef);
        var blob = client.GetBlobContainerClient(location.Container).GetBlobClient(location.Name);
        var response = await blob.ExistsAsync(cancellationToken);
        return response.Value;
    }

    /// <inheritdoc />
    public async Task<BlobListing> ListAsync(BlobListRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Container, nameof(request));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.PageSize, nameof(request));

        var container = client.GetBlobContainerClient(request.Container);
        var prefix = string.IsNullOrEmpty(request.Prefix) ? null : request.Prefix;
        var continuationToken = string.IsNullOrEmpty(request.ContinuationToken) ? null : request.ContinuationToken;
        var blobs = new List<BlobMetadata>();
        var prefixes = new List<string>();
        string? nextToken = null;

        // The service pages natively (continuation tokens are Azure's own). A blob without Elarion
        // state metadata (written by another producer) reads as Committed. The lifecycle-state filter
        // is applied per page after listing — Azure cannot filter by metadata server-side — so a
        // filtered page may carry fewer items than PageSize while more remain (documented on
        // BlobListRequest.State).
        try {
            if (request.Delimiter is { Length: > 0 } delimiter) {
                var pageable = container.GetBlobsByHierarchyAsync(
                    BlobTraits.Metadata, BlobStates.None, delimiter, prefix, cancellationToken);
                await foreach (var page in pageable.AsPages(continuationToken, request.PageSize)) {
                    foreach (var item in page.Values) {
                        if (item.IsPrefix) {
                            prefixes.Add(item.Prefix);
                        }
                        else {
                            AddIfMatchesState(blobs, request, item.Blob);
                        }
                    }

                    nextToken = string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken;
                    break;
                }
            }
            else {
                var pageable = container.GetBlobsAsync(
                    new GetBlobsOptions { Traits = BlobTraits.Metadata, Prefix = prefix }, cancellationToken);
                await foreach (var page in pageable.AsPages(continuationToken, request.PageSize)) {
                    foreach (var item in page.Values) {
                        AddIfMatchesState(blobs, request, item);
                    }

                    nextToken = string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken;
                    break;
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404) {
            // A missing container yields an empty listing, matching the relational store.
        }

        return new BlobListing {
            Blobs = blobs,
            Prefixes = prefixes,
            ContinuationToken = nextToken,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListContainersAsync(CancellationToken cancellationToken) {
        var names = new List<string>();
        await foreach (var containerItem in client.GetBlobContainersAsync(cancellationToken: cancellationToken)) {
            names.Add(containerItem.Name);
        }

        return names;
    }

    private void AddIfMatchesState(List<BlobMetadata> blobs, BlobListRequest request, BlobItem item) {
        var state = item.Metadata is not null && AzureBlobMetadata.IsPending(item.Metadata)
            ? BlobLifecycleState.Pending
            : BlobLifecycleState.Committed;
        if (request.State is { } filter && filter != state) {
            return;
        }

        var location = new AzureBlobLocation(request.Container, item.Name);
        blobs.Add(new BlobMetadata {
            Id = location.ToBlobRef().Value,
            Container = location.Container,
            Name = location.Name,
            ContentType = item.Properties.ContentType ?? "application/octet-stream",
            Size = item.Properties.ContentLength ?? 0,
            CreatedAt = item.Properties.CreatedOn ?? default,
            State = state,
            OwnerId = item.Metadata is null
                ? null
                : AzureBlobMetadata.Decode(item.Metadata, AzureBlobMetadata.OwnerKey),
        });
    }

    /// <inheritdoc />
    public async Task<bool> CommitAsync(BlobRef blobRef, CancellationToken cancellationToken) {
        var location = AzureBlobLocation.Parse(blobRef);
        var blob = client.GetBlobContainerClient(location.Container).GetBlobClient(location.Name);

        // ETag-guarded metadata flip, retried on concurrent modification: the garbage collector deletes
        // only with the ETag it listed, so whichever of commit/collect lands first invalidates the other.
        for (var attempt = 0; ; attempt++) {
            BlobProperties properties;
            try {
                properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) {
                return false;
            }

            if (!AzureBlobMetadata.IsPending(properties.Metadata)) {
                return true;
            }

            var metadata = new Dictionary<string, string>(properties.Metadata, StringComparer.Ordinal) {
                [AzureBlobMetadata.StateKey] = AzureBlobMetadata.CommittedState,
            };
            metadata.Remove(AzureBlobMetadata.ExpiresAtKey);

            try {
                await blob.SetMetadataAsync(
                    metadata,
                    new BlobRequestConditions { IfMatch = properties.ETag },
                    cancellationToken);
                logger.LogDebug("Committed blob {BlobRef}", blobRef.Value);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 412 && attempt < 3) {
                // Concurrently modified; re-read and retry.
            }
            catch (RequestFailedException ex) when (ex.Status == 404) {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteExpiredPendingAsync(
        DateTimeOffset olderThanUtc,
        int batchSize,
        CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        // Metadata-filtered listing across the account's containers. Only blobs carrying the Elarion
        // state metadata are ever considered, so foreign blobs in shared accounts are untouched. Each
        // delete is ETag-guarded, so a blob committed between the listing and the delete is left alone.
        var deleted = 0;
        await foreach (var containerItem in client.GetBlobContainersAsync(cancellationToken: cancellationToken)) {
            var container = client.GetBlobContainerClient(containerItem.Name);
            var listing = container.GetBlobsAsync(
                new GetBlobsOptions { Traits = BlobTraits.Metadata }, cancellationToken);
            await foreach (var blobItem in listing) {
                if (blobItem.Metadata is null || !AzureBlobMetadata.IsPending(blobItem.Metadata)) {
                    continue;
                }

                if (AzureBlobMetadata.ParseInstant(blobItem.Metadata, AzureBlobMetadata.ExpiresAtKey)
                    is not { } expiresAt || expiresAt >= olderThanUtc) {
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
                    // Committed (or replaced) concurrently; leave it alone.
                }

                if (deleted >= batchSize) {
                    LogCollected(deleted);
                    return deleted;
                }
            }
        }

        LogCollected(deleted);
        return deleted;
    }

    internal async Task<BlobContainerClient> EnsureContainerAsync(string containerName, CancellationToken cancellationToken) {
        var container = client.GetBlobContainerClient(containerName);
        if (_ensuredContainers.ContainsKey(containerName)) {
            return container;
        }

        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        _ensuredContainers.TryAdd(containerName, 0);
        return container;
    }

    private void LogCollected(int deleted) {
        if (deleted > 0) {
            logger.LogInformation("Garbage collected {Count} expired pending blob(s).", deleted);
        }
    }

    private static BlobMetadata ToMetadata(AzureBlobLocation location, BlobProperties properties) =>
        new() {
            Id = location.ToBlobRef().Value,
            Container = location.Container,
            Name = location.Name,
            ContentType = properties.ContentType,
            Size = properties.ContentLength,
            CreatedAt = properties.CreatedOn,
            State = AzureBlobMetadata.IsPending(properties.Metadata)
                ? BlobLifecycleState.Pending
                : BlobLifecycleState.Committed,
            OwnerId = AzureBlobMetadata.Decode(properties.Metadata, AzureBlobMetadata.OwnerKey),
        };

    private static void ValidateUploadRequest(BlobUploadRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Container, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContentType, nameof(request));
    }
}
