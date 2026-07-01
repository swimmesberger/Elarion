using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Blobs.Tus;

/// <summary>
/// In-memory <see cref="ITusUploadStore"/>: the single-instance default. Staged bytes live in process, so
/// in-progress uploads do not survive a restart and are not shared across instances — use a durable
/// provider-backed store (for example PostgreSQL staging) for those guarantees.
/// </summary>
/// <remarks>
/// Registered as a singleton so staging state persists across the requests of one upload; the scoped
/// <see cref="IBlobStore"/> is resolved on a fresh scope at finalization (the completed blob is its own
/// unit of work).
/// </remarks>
public sealed class InMemoryTusUploadStore(
    IServiceScopeFactory scopeFactory,
    TusOptions options,
    TimeProvider timeProvider) : ITusUploadStore {
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<TusUpload> CreateAsync(TusUploadCreation creation, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(creation);

        var id = Guid.NewGuid().ToString("N");
        var upload = new TusUpload {
            Id = id,
            Length = creation.Length,
            Offset = 0,
            ContentType = creation.ContentType,
            Metadata = creation.Metadata,
            OwnerId = creation.OwnerId,
            ExpiresAt = timeProvider.GetUtcNow() + options.UploadExpiry,
            BlobRef = null,
        };

        _entries[id] = new Entry(creation.Container, creation.Name, upload);
        return Task.FromResult(upload);
    }

    /// <inheritdoc />
    public Task<TusUpload?> GetAsync(string uploadId, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.TryGetValue(uploadId, out var entry) ? entry.Upload : null);

    /// <inheritdoc />
    public async Task<TusUpload> AppendAsync(
        string uploadId,
        long offset,
        Stream chunk,
        long? chunkLength,
        CancellationToken cancellationToken) {
        if (!_entries.TryGetValue(uploadId, out var entry)) {
            throw new TusOffsetConflictException();
        }

        await entry.Gate.WaitAsync(cancellationToken);
        try {
            var upload = entry.Upload;
            if (offset != upload.Offset) {
                throw new TusOffsetConflictException();
            }

            var remaining = upload.Length - upload.Offset;
            var written = await CopyAtMostAsync(chunk, entry.Buffer, remaining, cancellationToken);
            upload = upload with { Offset = upload.Offset + written };

            if (upload.Offset >= upload.Length && upload.BlobRef is null) {
                upload = upload with { BlobRef = await FinalizeAsync(entry, upload, cancellationToken) };
            }

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

        var expired = _entries
            .Where(pair => pair.Value.Upload.BlobRef is null && pair.Value.Upload.ExpiresAt < olderThanUtc)
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

    private async Task<BlobRef> FinalizeAsync(Entry entry, TusUpload upload, CancellationToken cancellationToken) {
        entry.Buffer.Position = 0;

        // Resolve the (scoped) blob store on a fresh scope: finalizing the completed upload is its own unit
        // of work, independent of any request scope.
        await using var scope = scopeFactory.CreateAsyncScope();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var blobRef = await blobStore.SaveAsync(
            new BlobUploadRequest {
                Container = entry.Container,
                Name = entry.Name,
                ContentType = upload.ContentType,
                ContentLength = upload.Length,
                InitialState = BlobLifecycleState.Pending,
                ExpiresAt = timeProvider.GetUtcNow() + options.Ttl,
            },
            entry.Buffer,
            cancellationToken);

        // The bytes now live in the blob; drop the staging buffer but keep the (small) session record so a
        // client can still resolve the blob reference until the session expires.
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

    private sealed class Entry(string container, string name, TusUpload upload) {
        public string Container { get; } = container;

        public string Name { get; } = name;

        public TusUpload Upload { get; set; } = upload;

        public MemoryStream Buffer { get; set; } = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
