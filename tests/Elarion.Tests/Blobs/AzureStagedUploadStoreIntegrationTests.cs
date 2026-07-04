using AwesomeAssertions;
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

        var created = await store.CreateAsync(NewCreation(length: null), ct);
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

    private (AzureStagedUploadStore Store, AzureBlobStore BlobStore) CreateStores(string? stagingContainer = null) {
        var options = new AzureStagedUploadOptions();
        if (stagingContainer is not null) {
            options.StagingContainer = stagingContainer;
        }

        var store = new AzureStagedUploadStore(fixture.Client, options, NullLogger<AzureStagedUploadStore>.Instance);
        var blobStore = new AzureBlobStore(fixture.Client, NullLogger<AzureBlobStore>.Instance);
        return (store, blobStore);
    }

    private static StagedUploadCreation NewCreation(long? length) =>
        new() {
            Container = $"c{Guid.NewGuid():N}",
            Name = $"user-1/{Guid.NewGuid():N}/file.bin",
            Length = length,
            ContentType = "application/octet-stream",
            OwnerId = "user-1",
            ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(24),
        };

    private static StagedUploadCompletion NewCompletion() =>
        new() {
            SessionExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(1),
            BlobExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30),
        };
}
