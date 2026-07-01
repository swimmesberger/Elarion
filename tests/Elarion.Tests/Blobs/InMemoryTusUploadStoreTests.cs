using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.Tus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Fast tests for <see cref="InMemoryTusUploadStore"/> covering completion into a pending blob and the
/// garbage collection of both incomplete and completed sessions (M10).
/// </summary>
public sealed class InMemoryTusUploadStoreTests {
    [Fact]
    public async Task AppendAsync_Completes_ProducesPendingOwnerScopedBlob() {
        var ct = TestContext.Current.CancellationToken;
        var (store, blobStore) = CreateStore(out _);
        var created = await store.CreateAsync(NewCreation(3), ct);

        var completed = await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3]), ct);

        completed.BlobRef.Should().NotBeNull();
        blobStore.LastRequest!.InitialState.Should().Be(BlobLifecycleState.Pending);
        blobStore.LastRequest!.OwnerId.Should().Be("user-1");
        blobStore.Content(completed.BlobRef!.Value.Value).Should().Equal([1, 2, 3]);
    }

    [Fact]
    public async Task DeleteExpiredAsync_ReapsIncompleteAndCompletedPastExpiry() {
        var ct = TestContext.Current.CancellationToken;
        var origin = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(origin);
        // A short upload expiry so both the incomplete and the completed session become eligible.
        var (store, _) = CreateStore(out _, time, o => o.UploadExpiry = TimeSpan.FromMinutes(1));

        var incomplete = await store.CreateAsync(NewCreation(8), ct);
        var willComplete = await store.CreateAsync(NewCreation(2), ct);
        (await store.AppendAsync(willComplete.Id, 0, new MemoryStream([1, 2]), ct)).BlobRef.Should().NotBeNull();

        time.Advance(TimeSpan.FromMinutes(2));
        var deleted = await store.DeleteExpiredAsync(time.GetUtcNow(), 100, ct);

        deleted.Should().Be(2);
        (await store.GetAsync(incomplete.Id, ct)).Should().BeNull();
        (await store.GetAsync(willComplete.Id, ct)).Should().BeNull();
    }

    private static (InMemoryTusUploadStore Store, RecordingBlobStore BlobStore) CreateStore(
        out IServiceProvider provider,
        FakeTimeProvider? time = null,
        Action<TusOptions>? configure = null) {
        var blobStore = new RecordingBlobStore();
        var services = new ServiceCollection();
        services.AddScoped<IBlobStore>(_ => blobStore);
        provider = services.BuildServiceProvider();
        var options = new TusOptions();
        configure?.Invoke(options);
        var store = new InMemoryTusUploadStore(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            time ?? new FakeTimeProvider());
        return (store, blobStore);
    }

    private static TusUploadCreation NewCreation(long length) =>
        new() {
            Container = "uploads",
            Name = $"user-1/{Guid.NewGuid():N}/file.bin",
            Length = length,
            ContentType = "application/octet-stream",
            OwnerId = "user-1"
        };

    private sealed class RecordingBlobStore : IBlobStore {
        private readonly Dictionary<string, byte[]> _content = new(StringComparer.Ordinal);

        public BlobUploadRequest? LastRequest { get; private set; }

        public byte[] Content(string id) => _content[id];

        public async Task<BlobRef> SaveAsync(BlobUploadRequest request, Stream content, CancellationToken cancellationToken) {
            LastRequest = request;
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var id = Guid.NewGuid().ToString();
            _content[id] = buffer.ToArray();
            return new BlobRef { Value = id };
        }

        public Task<BlobMetadata?> GetMetadataAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
            Task.FromResult<BlobMetadata?>(null);

        public Task<bool> DeleteAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
            Task.FromResult(_content.Remove(blobRef.Value));

        public Task<bool> ExistsAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
            Task.FromResult(_content.ContainsKey(blobRef.Value));

        public Task<BlobDownload?> OpenReadAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
            Task.FromResult<BlobDownload?>(null);
    }
}
