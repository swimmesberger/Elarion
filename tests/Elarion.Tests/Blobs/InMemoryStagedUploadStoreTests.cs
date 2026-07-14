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

    [Fact]
    public async Task AppendAsync_FailedMidChunk_ResendProducesExactContent() {
        // A client disconnect mid-chunk leaves partial bytes; the offset must not advance and the
        // protocol-correct full resend must land after the last committed byte, not after the junk.
        var ct = TestContext.Current.CancellationToken;
        var (store, blobStore) = CreateStore();
        var created = await store.CreateAsync(NewCreation(4), ct);

        var failing = async () => await store.AppendAsync(
            created.Id, 0, new ThrowingAfterBytesStream([1, 2]), ct);
        await failing.Should().ThrowAsync<IOException>();
        (await store.GetAsync(created.Id, ct))!.Offset.Should().Be(0);

        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3, 4]), ct);
        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);

        blobStore.Content(completed.BlobRef!.Value.Value).Should().Equal([1, 2, 3, 4]);
    }

    [Fact]
    public async Task AppendAsync_FailedMidChunk_DeferredLength_ResendProducesExactContent() {
        // Deferred-length sessions take the unbounded copy path; a mid-chunk failure must not leave
        // junk that a resend then appends after.
        var ct = TestContext.Current.CancellationToken;
        var (store, blobStore) = CreateStore();
        var created = await store.CreateAsync(NewCreation(length: null), ct);

        var failing = async () => await store.AppendAsync(
            created.Id, 0, new ThrowingAfterBytesStream([9, 9, 9]), ct);
        await failing.Should().ThrowAsync<IOException>();

        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3]), ct);
        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);

        completed.Length.Should().Be(3);
        blobStore.Content(completed.BlobRef!.Value.Value).Should().Equal([1, 2, 3]);
    }

    [Fact]
    public async Task DeleteAsync_RacingInFlightAppend_ConflictsInsteadOfObjectDisposed() {
        // A delete racing an in-flight append must produce the same conflict semantics as the durable
        // stores (the session is simply gone), never an ObjectDisposedException from the reaped buffer.
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStore();
        var created = await store.CreateAsync(NewCreation(4), ct);

        var slowChunk = new BlockingStream([1, 2, 3, 4]);
        var inFlight = store.AppendAsync(created.Id, 0, slowChunk, ct);
        await slowChunk.ReadStarted;

        // The delete queues behind the in-flight append's gate; a follow-up append then sees the
        // removed session and conflicts.
        var delete = store.DeleteAsync(created.Id, ct);
        var lateAppend = async () => await store.AppendAsync(created.Id, 0, new MemoryStream([9]), ct);
        await lateAppend.Should().ThrowAsync<StagedUploadConflictException>();

        slowChunk.Release();
        (await inFlight).Offset.Should().Be(4);
        await delete;
        (await store.GetAsync(created.Id, ct)).Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_AfterDelete_Conflicts() {
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStore();
        var created = await store.CreateAsync(NewCreation(1), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1]), ct);
        await store.DeleteAsync(created.Id, ct);

        var act = async () => await store.CompleteAsync(created.Id, NewCompletion(), ct);

        await act.Should().ThrowAsync<StagedUploadConflictException>();
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

    /// <summary>Serves its payload on the first read, then fails the copy like a client disconnect.</summary>
    private sealed class ThrowingAfterBytesStream(byte[] payload) : Stream {
        private bool _served;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) {
            if (_served) {
                throw new IOException("The client disconnected mid-chunk.");
            }

            _served = true;
            payload.CopyTo(buffer);
            return ValueTask.FromResult(payload.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Blocks the first read until released, so a test can hold an append in flight.</summary>
    private sealed class BlockingStream(byte[] payload) : Stream {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _served;

        public Task ReadStarted => _readStarted.Task;

        public void Release() => _released.TrySetResult();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) {
            _readStarted.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            if (_served) {
                return 0;
            }

            _served = true;
            payload.CopyTo(buffer);
            return payload.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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

        public Task<BlobListing> ListAsync(BlobListRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListContainersAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
