using AwesomeAssertions;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Elarion.Blobs;
using Elarion.Blobs.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Integration tests for <see cref="AzureStagedUploadStore"/> against Azurite — the seam-validation
/// exercise: every resumable-upload operation on the bare Azure SDK (append blobs with append-position
/// preconditions, server-side completion copy into a pending blob readable through
/// <see cref="AzureBlobStore"/>). Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AzureStagedUploadStoreIntegrationTests(AzuriteFixture fixture) : IClassFixture<AzuriteFixture> {
    [Fact]
    public async Task CreateAppendComplete_ProducesPendingBlobWithContent() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (store, blobStore) = CreateStores();
        var payload = new byte[] { 10, 20, 30, 40, 50 };

        var created = await store.CreateAsync(NewCreation(payload.Length), ct);
        created.Offset.Should().Be(0);

        var appended = await store.AppendAsync(created.Id, 0, new MemoryStream(payload), ct);
        appended.Offset.Should().Be(5);
        appended.BlobRef.Should().BeNull();

        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);

        completed.BlobRef.Should().NotBeNull();
        // The completed blob is a first-class AzureBlobStore blob: readable, owned, and pending (the
        // commit path works on it).
        (await blobStore.ReadAllBytesAsync(completed.BlobRef!.Value, ct)).Should().Equal(payload);
        (await blobStore.GetMetadataAsync(completed.BlobRef!.Value, ct))!.OwnerId.Should().Be("user-1");
        (await blobStore.CommitAsync(completed.BlobRef!.Value, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task ResumableAppend_TwoChunks_TracksOffsetAcrossProbes() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (store, blobStore) = CreateStores();

        var created = await store.CreateAsync(NewCreation(6), ct);
        (await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3]), ct)).Offset.Should().Be(3);

        // The offset survives a status probe from any node — it is the append blob's committed length.
        (await store.GetAsync(created.Id, ct))!.Offset.Should().Be(3);

        await store.AppendAsync(created.Id, 3, new MemoryStream([4, 5, 6]), ct);
        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);
        (await blobStore.ReadAllBytesAsync(completed.BlobRef!.Value, ct)).Should().Equal([1, 2, 3, 4, 5, 6]);
    }

    [Fact]
    public async Task AppendAsync_SplitsChunksLargerThanOneAppendBlock() {
        // Exercises the block-splitting loop: a chunk larger than the 4 MiB append-block cap lands as
        // multiple position-guarded blocks.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (store, blobStore) = CreateStores();
        var payload = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        var created = await store.CreateAsync(NewCreation(payload.Length), ct);
        var appended = await store.AppendAsync(created.Id, 0, new MemoryStream(payload), ct);
        appended.Offset.Should().Be(payload.Length);

        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);
        (await blobStore.ReadAllBytesAsync(completed.BlobRef!.Value, ct)).Should().Equal(payload);
    }

    [Fact]
    public async Task AppendAsync_WrongOffset_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStores();
        var created = await store.CreateAsync(NewCreation(4), ct);

        var act = async () => await store.AppendAsync(created.Id, 2, new MemoryStream([1, 2]), ct);

        await act.Should().ThrowAsync<StagedUploadConflictException>();
    }

    [Fact]
    public async Task AppendAsync_AfterCompletion_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStores();
        var created = await store.CreateAsync(NewCreation(1), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1]), ct);
        await store.CompleteAsync(created.Id, NewCompletion(), ct);

        var act = async () => await store.AppendAsync(created.Id, 1, new MemoryStream([2]), ct);

        await act.Should().ThrowAsync<StagedUploadConflictException>();
    }

    [Fact]
    public async Task CompleteAsync_ZeroLength_ProducesEmptyBlobWithoutAppend() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (store, blobStore) = CreateStores();

        var created = await store.CreateAsync(NewCreation(0), ct);
        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);

        completed.BlobRef.Should().NotBeNull();
        (await blobStore.ReadAllBytesAsync(completed.BlobRef!.Value, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteAsync_BeforeDeclaredLengthReached_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStores();
        var created = await store.CreateAsync(NewCreation(4), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2]), ct);

        var act = async () => await store.CompleteAsync(created.Id, NewCompletion(), ct);

        await act.Should().ThrowAsync<StagedUploadConflictException>();
    }

    [Fact]
    public async Task CompleteAsync_IsIdempotent_AndSessionStaysQueryable() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStores();
        var created = await store.CreateAsync(NewCreation(2), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2]), ct);

        var first = await store.CompleteAsync(created.Id, NewCompletion(), ct);
        var second = await store.CompleteAsync(created.Id, NewCompletion(), ct);
        second.BlobRef.Should().Be(first.BlobRef);

        // A status probe still resolves the reference (and the final offset) until retention elapses,
        // even though the staged bytes were dropped at completion.
        var probed = await store.GetAsync(created.Id, ct);
        probed!.BlobRef.Should().Be(first.BlobRef);
        probed.Offset.Should().Be(2);
        probed.Length.Should().Be(2);
    }

    [Fact]
    public async Task DeferredLength_SealsAtReceivedBytes() {
        // The RUFH shape: no declared length, growth per append, sealed by the explicit completion.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (store, blobStore) = CreateStores();

        var created = await store.CreateAsync(NewCreation(null), ct);
        created.Length.Should().BeNull();

        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3]), ct);
        await store.AppendAsync(created.Id, 3, new MemoryStream([4, 5]), ct);
        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);

        completed.Length.Should().Be(5);
        (await blobStore.ReadAllBytesAsync(completed.BlobRef!.Value, ct)).Should().Equal([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSession() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (store, _) = CreateStores();
        var created = await store.CreateAsync(NewCreation(4), ct);

        await store.DeleteAsync(created.Id, ct);

        (await store.GetAsync(created.Id, ct)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteExpiredAsync_ReapsExpiredSessions() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        // A dedicated staging container isolates this sweep from concurrent tests.
        var (store, _) = CreateStores($"staging-{Guid.NewGuid():N}");

        var expiredIncomplete = await store.CreateAsync(
            NewCreation(8) with { ExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5) }, ct);
        var fresh = await store.CreateAsync(NewCreation(8), ct);
        var reapedCompleted = await store.CreateAsync(NewCreation(1), ct);
        await store.AppendAsync(reapedCompleted.Id, 0, new MemoryStream([1]), ct);
        await store.CompleteAsync(
            reapedCompleted.Id,
            NewCompletion() with { SessionExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1) },
            ct);

        var deleted = await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, 100, ct);

        deleted.Should().Be(2);
        (await store.GetAsync(expiredIncomplete.Id, ct)).Should().BeNull();
        (await store.GetAsync(reapedCompleted.Id, ct)).Should().BeNull();
        (await store.GetAsync(fresh.Id, ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task CompleteAsync_AppendLandingAfterSnapshot_ThrowsConflict() {
        // A deferred-length append landing between completion's status read and the server-side copy
        // changes the staging blob's ETag; the copy's source precondition must reject it instead of
        // silently completing without the late bytes.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var staging = $"staging-{Guid.NewGuid():N}";
        var (store, _) = CreateStores(staging);
        var created = await store.CreateAsync(NewCreation(null), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3]), ct);

        var interceptedStore = CreateInterceptedStore(
            staging,
            request => request.Method == RequestMethod.Head && request.Uri.ToString().Contains(created.Id),
            () => AppendRawByteAsync(staging, created.Id, ct));

        var act = async () => await interceptedStore.CompleteAsync(created.Id, NewCompletion(), ct);
        await act.Should().ThrowAsync<StagedUploadConflictException>();

        // The session stays incomplete with every byte intact; a retry completes from a fresh snapshot.
        var probed = await store.GetAsync(created.Id, ct);
        probed!.BlobRef.Should().BeNull();
        probed.Offset.Should().Be(4);
    }

    [Fact]
    public async Task CompleteAsync_AppendLandingAfterCopy_ThrowsConflictAndPreservesLateBytes() {
        // A deferred-length append landing between the server-side copy and the staging blob's
        // recreate-with-marker used to be silently destroyed by the unconditional overwrite; the ETag
        // precondition must surface the race as a conflict and leave the late bytes in place.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var staging = $"staging-{Guid.NewGuid():N}";
        var (store, _) = CreateStores(staging);
        var created = await store.CreateAsync(NewCreation(null), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3]), ct);

        var interceptedStore = CreateInterceptedStore(
            staging,
            request => request.Method == RequestMethod.Put && request.Headers.TryGetValue("x-ms-copy-source", out _),
            () => AppendRawByteAsync(staging, created.Id, ct));

        var act = async () => await interceptedStore.CompleteAsync(created.Id, NewCompletion(), ct);
        await act.Should().ThrowAsync<StagedUploadConflictException>();

        var probed = await store.GetAsync(created.Id, ct);
        probed!.BlobRef.Should().BeNull();
        probed.Offset.Should().Be(4);
    }

    [Fact]
    public async Task CompleteAsync_DeleteLandingAfterCopy_ThrowsConflictInsteadOfResurrecting() {
        // A tus DELETE landing in the same window used to be resurrected by the unconditional recreate;
        // with the precondition the completion conflicts and the session stays gone.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var staging = $"staging-{Guid.NewGuid():N}";
        var (store, _) = CreateStores(staging);
        var created = await store.CreateAsync(NewCreation(2), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2]), ct);

        var interceptedStore = CreateInterceptedStore(
            staging,
            request => request.Method == RequestMethod.Put && request.Headers.TryGetValue("x-ms-copy-source", out _),
            () => store.DeleteAsync(created.Id, ct));

        var act = async () => await interceptedStore.CompleteAsync(created.Id, NewCompletion(), ct);
        await act.Should().ThrowAsync<StagedUploadConflictException>();

        (await store.GetAsync(created.Id, ct)).Should().BeNull();
    }

    private AzureStagedUploadStore CreateInterceptedStore(
        string stagingContainer,
        Func<Request, bool> trigger,
        Func<Task> mutation) {
        var clientOptions = new BlobClientOptions();
        clientOptions.AddPolicy(new MutateAfterResponsePolicy(trigger, mutation), HttpPipelinePosition.PerCall);
        var client = new BlobServiceClient(fixture.ConnectionString, clientOptions);
        return new AzureStagedUploadStore(
            client,
            new AzureStagedUploadOptions { StagingContainer = stagingContainer },
            NullLogger<AzureStagedUploadStore>.Instance);
    }

    private async Task AppendRawByteAsync(string stagingContainer, string uploadId, CancellationToken ct) {
        await fixture.Client.GetBlobContainerClient(stagingContainer)
            .GetAppendBlobClient(uploadId)
            .AppendBlockAsync(new MemoryStream([7]), cancellationToken: ct);
    }

    /// <summary>Runs a one-shot side effect after the first response matching the trigger, so a test can
    /// deterministically interleave a concurrent mutation inside a multi-request operation.</summary>
    private sealed class MutateAfterResponsePolicy(Func<Request, bool> trigger, Func<Task> mutation)
        : HttpPipelinePolicy {
        private int _fired;

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) {
            await ProcessNextAsync(message, pipeline);
            if (trigger(message.Request) && Interlocked.Exchange(ref _fired, 1) == 0) await mutation();
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) {
            ProcessNext(message, pipeline);
        }
    }

    private (AzureStagedUploadStore Store, AzureBlobStore BlobStore) CreateStores(string? stagingContainer = null) {
        var options = new AzureStagedUploadOptions();
        if (stagingContainer is not null) options.StagingContainer = stagingContainer;

        var store = new AzureStagedUploadStore(fixture.Client, options, NullLogger<AzureStagedUploadStore>.Instance);
        var blobStore = new AzureBlobStore(fixture.Client, NullLogger<AzureBlobStore>.Instance);
        return (store, blobStore);
    }

    private static StagedUploadCreation NewCreation(long? length) {
        return new StagedUploadCreation {
            Container = $"c{Guid.NewGuid():N}",
            Name = $"user-1/{Guid.NewGuid():N}/file.bin",
            Length = length,
            ContentType = "application/octet-stream",
            OwnerId = "user-1",
            ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(24)
        };
    }

    private static StagedUploadCompletion NewCompletion() {
        return new StagedUploadCompletion {
            SessionExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(1),
            BlobExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30)
        };
    }
}
