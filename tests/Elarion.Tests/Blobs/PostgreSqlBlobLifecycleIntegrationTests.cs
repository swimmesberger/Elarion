using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Integration tests for the pending/commit/garbage-collection lifecycle of
/// <see cref="PostgreSqlBlobStore{TDbContext}"/> against a real PostgreSQL instance. Each test uses a
/// unique container name so they stay isolated, and skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlBlobLifecycleIntegrationTests(PostgreSqlBlobStoreFixture fixture)
    : IClassFixture<PostgreSqlBlobStoreFixture> {
    [Fact]
    public async Task SaveAsync_Pending_WritesStateAndExpiry() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var expiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var request = NewPendingRequest(expiresAt);

        await using (var context = fixture.CreateContext()) {
            await CreateStore(context).SaveAsync(request, new MemoryStream(Bytes(128)), ct);
        }

        await using var verify = fixture.CreateContext();
        var row = await Row(verify, request, ct);
        row!.State.Should().Be(BlobLifecycleState.Pending);
        row.ExpiresAt.Should().Be(expiresAt);
    }

    [Fact]
    public async Task SaveAsync_Default_IsCommittedWithoutExpiry() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var request = NewCommittedRequest();

        await using (var context = fixture.CreateContext()) {
            await CreateStore(context).SaveAsync(request, new MemoryStream(Bytes(64)), ct);
        }

        await using var verify = fixture.CreateContext();
        var row = await Row(verify, request, ct);
        row!.State.Should().Be(BlobLifecycleState.Committed);
        row.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task CommitAsync_PromotesPendingAndClearsExpiry() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var request = NewPendingRequest(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var blobRef = await SavePending(request, ct);

        await using (var commitContext = fixture.CreateContext()) {
            var lifecycle = (IBlobLifecycle)CreateStore(commitContext);
            (await lifecycle.CommitAsync(blobRef, ct)).Should().BeTrue();
            await commitContext.SaveChangesAsync(ct);
        }

        await using var verify = fixture.CreateContext();
        var row = await Row(verify, request, ct);
        row!.State.Should().Be(BlobLifecycleState.Committed);
        row.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task CommitAsync_AlreadyCommitted_IsIdempotent() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var request = NewCommittedRequest();
        var blobRef = await SaveCommitted(request, ct);

        await using var commitContext = fixture.CreateContext();
        var lifecycle = (IBlobLifecycle)CreateStore(commitContext);

        (await lifecycle.CommitAsync(blobRef, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_MissingBlob_ReturnsFalse() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        await using var context = fixture.CreateContext();
        var lifecycle = (IBlobLifecycle)CreateStore(context);

        (await lifecycle.CommitAsync(new BlobRef { Value = Guid.NewGuid().ToString() }, ct))
            .Should().BeFalse();
    }

    [Fact]
    public async Task DeleteExpiredPendingAsync_DeletesOnlyExpiredPending() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var expiredPending = NewPendingRequest(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var futurePending = NewPendingRequest(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var committed = NewCommittedRequest();
        var expiredRef = await SavePending(expiredPending, ct);
        var futureRef = await SavePending(futurePending, ct);
        var committedRef = await SaveCommitted(committed, ct);

        int deleted;
        await using (var gcContext = fixture.CreateContext()) {
            var lifecycle = (IBlobLifecycle)CreateStore(gcContext);
            deleted = await lifecycle.DeleteExpiredPendingAsync(
                new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), 100, ct);
        }

        deleted.Should().Be(1);
        await using var verify = fixture.CreateContext();
        (await Row(verify, expiredPending, ct)).Should().BeNull();
        (await Row(verify, futurePending, ct)).Should().NotBeNull();
        (await Row(verify, committed, ct)).Should().NotBeNull();
        // Content of the surviving blobs is untouched; the deleted blob's content cascaded away.
        (await ContentRowCount(verify, expiredRef, ct)).Should().Be(0);
        (await ContentRowCount(verify, futureRef, ct)).Should().Be(1);
        (await ContentRowCount(verify, committedRef, ct)).Should().Be(1);
    }

    [Fact]
    public async Task DeleteExpiredPendingAsync_RespectsBatchSize() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var expired = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var a = NewPendingRequest(expired);
        var b = NewPendingRequest(expired);
        var c = NewPendingRequest(expired);
        await SavePending(a, ct);
        await SavePending(b, ct);
        await SavePending(c, ct);

        await using var gcContext = fixture.CreateContext();
        var lifecycle = (IBlobLifecycle)CreateStore(gcContext);

        var first = await lifecycle.DeleteExpiredPendingAsync(DateTimeOffset.UtcNow, 2, ct);
        var second = await lifecycle.DeleteExpiredPendingAsync(DateTimeOffset.UtcNow, 2, ct);

        first.Should().Be(2);
        second.Should().Be(1);
    }

    [Fact]
    public async Task Commit_BeforeSweep_KeepsBlob() {
        // Race row 1/2: once committed, the partial index no longer matches, so the sweep skips it.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var request = NewPendingRequest(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var blobRef = await SavePending(request, ct);

        await using (var commitContext = fixture.CreateContext()) {
            var lifecycle = (IBlobLifecycle)CreateStore(commitContext);
            (await lifecycle.CommitAsync(blobRef, ct)).Should().BeTrue();
            await commitContext.SaveChangesAsync(ct);
        }

        await using (var gcContext = fixture.CreateContext()) {
            var lifecycle = (IBlobLifecycle)CreateStore(gcContext);
            (await lifecycle.DeleteExpiredPendingAsync(DateTimeOffset.UtcNow, 100, ct)).Should().Be(0);
        }

        await using var verify = fixture.CreateContext();
        (await Row(verify, request, ct))!.State.Should().Be(BlobLifecycleState.Committed);
    }

    [Fact]
    public async Task Sweep_BeforeCommit_LeavesNothingToCommit() {
        // Race row 3: the sweep wins, so a later commit finds nothing and returns false.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var request = NewPendingRequest(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var blobRef = await SavePending(request, ct);

        await using (var gcContext = fixture.CreateContext()) {
            var lifecycle = (IBlobLifecycle)CreateStore(gcContext);
            (await lifecycle.DeleteExpiredPendingAsync(DateTimeOffset.UtcNow, 100, ct)).Should().Be(1);
        }

        await using var commitContext = fixture.CreateContext();
        var commitLifecycle = (IBlobLifecycle)CreateStore(commitContext);
        (await commitLifecycle.CommitAsync(blobRef, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_PendingOverCommittedBlob_ThrowsAndLeavesRowCommitted() {
        // A committed blob is already referenced by application data; a pre-upload transport re-saving
        // its (container, name) as pending must fail loud instead of silently making the blob
        // GC-eligible.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var request = NewCommittedRequest();
        await SaveCommitted(request, ct);
        var downgrade = request with {
            InitialState = BlobLifecycleState.Pending,
            ExpiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        await using (var context = fixture.CreateContext()) {
            var act = async () => await CreateStore(context).SaveAsync(downgrade, new MemoryStream(Bytes(8)), ct);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        await using var verify = fixture.CreateContext();
        var row = await Row(verify, request, ct);
        row!.State.Should().Be(BlobLifecycleState.Committed);
        row.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_PendingOverPendingBlob_ReplacesIt() {
        // Re-staging an uncommitted (pending) blob under the same name stays allowed — only the
        // committed state is protected from the downgrade.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var request = NewPendingRequest(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await SavePending(request, ct);

        await using (var context = fixture.CreateContext()) {
            await CreateStore(context).SaveAsync(request, new MemoryStream(Bytes(16)), ct);
        }

        await using var verify = fixture.CreateContext();
        var row = await Row(verify, request, ct);
        row!.State.Should().Be(BlobLifecycleState.Pending);
        row.Size.Should().Be(16);
    }

    private async Task<BlobRef> SavePending(BlobUploadRequest request, CancellationToken ct) {
        await using var context = fixture.CreateContext();
        return await CreateStore(context).SaveAsync(request, new MemoryStream(Bytes(256)), ct);
    }

    private async Task<BlobRef> SaveCommitted(BlobUploadRequest request, CancellationToken ct) {
        await using var context = fixture.CreateContext();
        return await CreateStore(context).SaveAsync(request, new MemoryStream(Bytes(256)), ct);
    }

    private static PostgreSqlBlobStore<IntegrationBlobDbContext> CreateStore(IntegrationBlobDbContext context) =>
        new(context, NullLogger<PostgreSqlBlobStore<IntegrationBlobDbContext>>.Instance, TimeProvider.System);

    private static Task<StoredBlob?> Row(
        IntegrationBlobDbContext context,
        BlobUploadRequest request,
        CancellationToken ct) =>
        context.Set<StoredBlob>()
            .AsNoTracking()
            .FirstOrDefaultAsync(blob => blob.Container == request.Container && blob.Name == request.Name, ct);

    private static async Task<long> ContentRowCount(
        IntegrationBlobDbContext context,
        BlobRef blobRef,
        CancellationToken ct) =>
        await context.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM blob_contents WHERE blob_id = {0}", blobRef.Value)
            .SingleAsync(ct);

    private static BlobUploadRequest NewPendingRequest(DateTimeOffset expiresAt) =>
        NewCommittedRequest() with { InitialState = BlobLifecycleState.Pending, ExpiresAt = expiresAt };

    private static BlobUploadRequest NewCommittedRequest() =>
        new() {
            Container = $"c-{Guid.NewGuid():N}",
            Name = "blob.bin",
            ContentType = "application/octet-stream"
        };

    private static byte[] Bytes(int count) {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++) {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }
}
