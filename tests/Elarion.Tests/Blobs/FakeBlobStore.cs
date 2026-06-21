using Elarion.Blobs;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Deterministic in-memory <see cref="IBlobStore"/> used to exercise <see cref="BlobStoreExtensions"/>
/// without a database. Captures the most recent save and serves seeded blobs for reads.
/// </summary>
internal sealed class FakeBlobStore : IBlobStore {
    private readonly Dictionary<string, (BlobMetadata Metadata, byte[] Data)> _blobs = new(StringComparer.Ordinal);

    public BlobUploadRequest? LastRequest { get; private set; }

    public byte[]? LastContent { get; private set; }

    public bool LastContentStreamWasReadable { get; private set; }

    public void Seed(BlobRef blobRef, BlobMetadata metadata, byte[] data) =>
        _blobs[blobRef.Value] = (metadata, data);

    public async Task<BlobRef> SaveAsync(BlobUploadRequest request, Stream content, CancellationToken cancellationToken) {
        LastRequest = request;
        LastContentStreamWasReadable = content.CanRead;

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        LastContent = buffer.ToArray();

        return new BlobRef { Value = $"{request.Container}/{request.Name}" };
    }

    public Task<BlobDownload?> OpenReadAsync(BlobRef blobRef, CancellationToken cancellationToken) {
        if (!_blobs.TryGetValue(blobRef.Value, out var entry)) {
            return Task.FromResult<BlobDownload?>(null);
        }

        var download = new BlobDownload(entry.Metadata, new MemoryStream(entry.Data, writable: false));
        return Task.FromResult<BlobDownload?>(download);
    }

    public Task<BlobMetadata?> GetMetadataAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
        Task.FromResult(_blobs.TryGetValue(blobRef.Value, out var entry) ? entry.Metadata : null);

    public Task<bool> DeleteAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
        Task.FromResult(_blobs.Remove(blobRef.Value));

    public Task<bool> ExistsAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
        Task.FromResult(_blobs.ContainsKey(blobRef.Value));
}
