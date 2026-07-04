using AwesomeAssertions;
using Elarion.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Fast tests for <see cref="InMemoryStagedUploadStore"/> covering explicit completion into a pending
/// blob, deferred-length sessions, conflicts, and the garbage collection of both incomplete and
/// completed sessions.
/// </summary>
public sealed class InMemoryStagedUploadStoreTests {
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompleteAsync_ProducesPendingOwnerScopedBlob() {
        var ct = TestContext.Current.CancellationToken;
        var (store, blobStore) = CreateStore();
        var created = await store.CreateAsync(NewCreation(3), ct);

        var appended = await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3]), ct);
        appended.BlobRef.Should().BeNull();

        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);

        completed.BlobRef.Should().NotBeNull();
        completed.IsComplete.Should().BeTrue();
        blobStore.LastRequest!.InitialState.Should().Be(BlobLifecycleState.Pending);
        blobStore.LastRequest!.OwnerId.Should().Be("user-1");
        blobStore.LastRequest!.ExpiresAt.Should().Be(NewCompletion().BlobExpiresAt);
        blobStore.Content(completed.BlobRef!.Value.Value).Should().Equal([1, 2, 3]);
    }

    [Fact]
    public async Task CompleteAsync_IsIdempotent() {
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStore();
        var created = await store.CreateAsync(NewCreation(2), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2]), ct);

        var first = await store.CompleteAsync(created.Id, NewCompletion(), ct);
        var second = await store.CompleteAsync(created.Id, NewCompletion(), ct);

        second.BlobRef.Should().Be(first.BlobRef);
    }

    [Fact]
    public async Task CompleteAsync_BeforeDeclaredLengthReached_Throws() {
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStore();
        var created = await store.CreateAsync(NewCreation(4), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2]), ct);

        var act = async () => await store.CompleteAsync(created.Id, NewCompletion(), ct);

        await act.Should().ThrowAsync<StagedUploadConflictException>();
    }

    [Fact]
    public async Task DeferredLength_SealsAtReceivedBytes() {
        // The RUFH shape: no declared length, growth per append, sealed by the explicit completion.
        var ct = TestContext.Current.CancellationToken;
        var (store, blobStore) = CreateStore();
        var created = await store.CreateAsync(NewCreation(length: null), ct);
        created.Length.Should().BeNull();

        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3]), ct);
        await store.AppendAsync(created.Id, 3, new MemoryStream([4, 5]), ct);
        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);

        completed.Length.Should().Be(5);
        blobStore.Content(completed.BlobRef!.Value.Value).Should().Equal([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task AppendAsync_WrongOffset_Throws() {
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStore();
        var created = await store.CreateAsync(NewCreation(4), ct);

        var act = async () => await store.AppendAsync(created.Id, 2, new MemoryStream([1, 2]), ct);

        await act.Should().ThrowAsync<StagedUploadConflictException>();
    }

    [Fact]
    public async Task AppendAsync_AfterCompletion_Throws() {
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStore();
        var created = await store.CreateAsync(NewCreation(1), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1]), ct);
        await store.CompleteAsync(created.Id, NewCompletion(), ct);

        var act = async () => await store.AppendAsync(created.Id, 1, new MemoryStream([2]), ct);

        await act.Should().ThrowAsync<StagedUploadConflictException>();
    }

    [Fact]
    public async Task DeleteExpiredAsync_ReapsIncompleteAndCompletedPastExpiry() {
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStore();

        // An incomplete session past its upload expiry, and a completed one past its retention deadline.
        var incomplete = await store.CreateAsync(NewCreation(8) with { ExpiresAt = Origin.AddMinutes(1) }, ct);
        var willComplete = await store.CreateAsync(NewCreation(2), ct);
        await store.AppendAsync(willComplete.Id, 0, new MemoryStream([1, 2]), ct);
        await store.CompleteAsync(
            willComplete.Id,
            NewCompletion() with { SessionExpiresAt = Origin.AddMinutes(1) },
            ct);

        var deleted = await store.DeleteExpiredAsync(Origin.AddMinutes(2), 100, ct);

        deleted.Should().Be(2);
        (await store.GetAsync(incomplete.Id, ct)).Should().BeNull();
        (await store.GetAsync(willComplete.Id, ct)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteExpiredAsync_KeepsCompletedSessionWithinRetention() {
        // A completed session must live long enough for a status probe to fetch the blob reference.
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStore();
        var willComplete = await store.CreateAsync(NewCreation(2), ct);
        await store.AppendAsync(willComplete.Id, 0, new MemoryStream([1, 2]), ct);
        await store.CompleteAsync(willComplete.Id, NewCompletion(), ct);

        var deleted = await store.DeleteExpiredAsync(Origin.AddMinutes(2), 100, ct);

        deleted.Should().Be(0);
        var stillThere = await store.GetAsync(willComplete.Id, ct);
        stillThere.Should().NotBeNull();
        stillThere!.BlobRef.Should().NotBeNull();
    }

    private static (InMemoryStagedUploadStore Store, RecordingBlobStore BlobStore) CreateStore() {
        var blobStore = new RecordingBlobStore();
        var services = new ServiceCollection();
        services.AddScoped<IBlobStore>(_ => blobStore);
        var provider = services.BuildServiceProvider();
        var store = new InMemoryStagedUploadStore(provider.GetRequiredService<IServiceScopeFactory>());
        return (store, blobStore);
    }

    private static StagedUploadCreation NewCreation(long? length) =>
        new() {
            Container = "uploads",
            Name = $"user-1/{Guid.NewGuid():N}/file.bin",
            Length = length,
            ContentType = "application/octet-stream",
            OwnerId = "user-1",
            ExpiresAt = Origin.AddHours(24),
        };

    private static StagedUploadCompletion NewCompletion() =>
        new() {
            SessionExpiresAt = Origin.AddHours(1),
            BlobExpiresAt = Origin.AddMinutes(30),
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
