using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Blobs;

/// <summary>
/// In-memory <see cref="IStagedUploadStore"/>: the single-instance default. Staged bytes live in
/// process, so in-progress uploads do not survive a restart and are not shared across instances — use a
/// durable provider-backed store (for example PostgreSQL or Azure staging) for those guarantees.
/// </summary>
/// <remarks>
/// Registered as a singleton so staging state persists across the requests of one upload; the scoped
/// <see cref="IBlobStore"/> is resolved on a fresh scope at completion (the completed blob is its own
/// unit of work).
/// </remarks>
public sealed class InMemoryStagedUploadStore(IServiceScopeFactory scopeFactory) : IStagedUploadStore {
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<StagedUpload> CreateAsync(StagedUploadCreation creation, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(creation);

        var id = Guid.CreateVersion7().ToString("N");
        var upload = new StagedUpload {
            Id = id,
            Length = creation.Length,
            Offset = 0,
            ContentType = creation.ContentType,
            Metadata = creation.Metadata,
            OwnerId = creation.OwnerId,
            ExpiresAt = creation.ExpiresAt,
            BlobRef = null,
        };

        _entries[id] = new Entry(creation.Container, creation.Name, upload);
        return Task.FromResult(upload);
    }

    /// <inheritdoc />
    public Task<StagedUpload?> GetAsync(string uploadId, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.TryGetValue(uploadId, out var entry) ? entry.Upload : null);

    /// <inheritdoc />
    public async Task<StagedUpload> AppendAsync(
        string uploadId,
        long offset,
        Stream chunk,
        CancellationToken cancellationToken) {
        if (!_entries.TryGetValue(uploadId, out var entry)) {
            throw new StagedUploadConflictException($"Upload session '{uploadId}' does not exist.");
        }

        await entry.Gate.WaitAsync(cancellationToken);
        try {
            var upload = entry.Upload;
            if (upload.IsComplete) {
                throw new StagedUploadConflictException($"Upload session '{uploadId}' is already complete.");
            }

            if (offset != upload.Offset) {
                throw new StagedUploadConflictException(
                    $"The append offset {offset} does not match the current offset {upload.Offset}.");
            }

            // A declared length caps the read to the remaining bytes; a deferred length reads the caller's
            // whole (caller-bounded) chunk.
            var remaining = upload.Length is long length ? length - upload.Offset : long.MaxValue;
            var written = await CopyAtMostAsync(chunk, entry.Buffer, remaining, cancellationToken);
            upload = upload with { Offset = upload.Offset + written };

            entry.Upload = upload;
            return upload;
        }
        finally {
            entry.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<StagedUpload> CompleteAsync(
        string uploadId,
        StagedUploadCompletion completion,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(completion);

        if (!_entries.TryGetValue(uploadId, out var entry)) {
            throw new StagedUploadConflictException($"Upload session '{uploadId}' does not exist.");
        }

        await entry.Gate.WaitAsync(cancellationToken);
        try {
            var upload = entry.Upload;
            if (upload.IsComplete) {
                return upload;
            }

            if (upload.Length is long declared && upload.Offset != declared) {
                throw new StagedUploadConflictException(
                    $"Upload session '{uploadId}' declares {declared} bytes but received {upload.Offset}.");
            }

            var blobRef = await SaveBlobAsync(entry, upload, completion.BlobExpiresAt, cancellationToken);
            upload = upload with {
                // A deferred length seals at the received byte count.
                Length = upload.Offset,
                ExpiresAt = completion.SessionExpiresAt,
                BlobRef = blobRef,
            };

            entry.Upload = upload;
            return upload;
        }
        finally {
            entry.Gate.Release();
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string uploadId, CancellationToken cancellationToken) {
        if (_entries.TryRemove(uploadId, out var entry)) {
            entry.Buffer.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> DeleteExpiredAsync(DateTimeOffset olderThanUtc, int batchSize, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        // Reap by expiry regardless of completion: an incomplete session past its upload-expiry window,
        // and a completed session past its completed-retention window (stamped at completion), so session
        // records do not accumulate in memory unbounded.
        var expired = _entries
            .Where(pair => pair.Value.Upload.ExpiresAt < olderThanUtc)
            .Select(pair => pair.Key)
            .Take(batchSize)
            .ToList();

        foreach (var key in expired) {
            if (_entries.TryRemove(key, out var entry)) {
                entry.Buffer.Dispose();
            }
        }

        return Task.FromResult(expired.Count);
    }

    private async Task<BlobRef> SaveBlobAsync(
        Entry entry,
        StagedUpload upload,
        DateTimeOffset? blobExpiresAt,
        CancellationToken cancellationToken) {
        entry.Buffer.Position = 0;

        // Resolve the (scoped) blob store on a fresh scope: completing the upload is its own unit of
        // work, independent of any request scope.
        await using var scope = scopeFactory.CreateAsyncScope();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var blobRef = await blobStore.SaveAsync(
            new BlobUploadRequest {
                Container = entry.Container,
                Name = entry.Name,
                ContentType = upload.ContentType,
                ContentLength = upload.Offset,
                InitialState = BlobLifecycleState.Pending,
                ExpiresAt = blobExpiresAt,
                OwnerId = upload.OwnerId,
            },
            entry.Buffer,
            cancellationToken);

        // The bytes now live in the blob; drop the staging buffer but keep the (small) session record so
        // a client can still resolve the blob reference until the session expires.
        entry.Buffer.Dispose();
        entry.Buffer = new MemoryStream();
        return blobRef;
    }

    private static async Task<long> CopyAtMostAsync(Stream source, Stream destination, long max, CancellationToken cancellationToken) {
        if (max <= 0) {
            return 0;
        }

        var buffer = new byte[81920];
        long total = 0;
        while (total < max) {
            var toRead = (int)Math.Min(buffer.Length, max - total);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0) {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }

        return total;
    }

    private sealed class Entry(string container, string name, StagedUpload upload) {
        public string Container { get; } = container;

        public string Name { get; } = name;

        public StagedUpload Upload { get; set; } = upload;

        public MemoryStream Buffer { get; set; } = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
