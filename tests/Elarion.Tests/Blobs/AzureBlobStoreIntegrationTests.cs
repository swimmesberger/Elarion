using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Integration tests for <see cref="AzureBlobStore"/> against Azurite: round-trips, upsert-by-name,
/// and the pending/commit/garbage-collection lifecycle carried in blob metadata. Skips when Docker is
/// unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AzureBlobStoreIntegrationTests(AzuriteFixture fixture) : IClassFixture<AzuriteFixture> {
    [Fact]
    public async Task SaveAsync_RoundTripsContentAndMetadata() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var request = NewRequest(owner: "user-1");
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        var blobRef = await store.SaveAsync(request, new MemoryStream(payload), ct);

        (await store.ReadAllBytesAsync(blobRef, ct)).Should().Equal(payload);
        var metadata = await store.GetMetadataAsync(blobRef, ct);
        metadata!.Container.Should().Be(request.Container);
        metadata.Name.Should().Be(request.Name);
        metadata.ContentType.Should().Be("application/pdf");
        metadata.Size.Should().Be(payload.Length);
        metadata.OwnerId.Should().Be("user-1");
        (await store.ExistsAsync(blobRef, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_SameName_OverwritesAndKeepsReference() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var request = NewRequest();

        var first = await store.SaveAsync(request, new MemoryStream([1, 2]), ct);
        var second = await store.SaveAsync(request, new MemoryStream([3, 4, 5]), ct);

        second.Should().Be(first);
        (await store.ReadAllBytesAsync(first, ct)).Should().Equal([3, 4, 5]);
    }

    [Fact]
    public async Task DeleteAsync_RemovesBlob() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var blobRef = await store.SaveAsync(NewRequest(), new MemoryStream([1]), ct);

        (await store.DeleteAsync(blobRef, ct)).Should().BeTrue();
        (await store.ExistsAsync(blobRef, ct)).Should().BeFalse();
        (await store.DeleteAsync(blobRef, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Read_MissingBlob_ReturnsNull() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var missing = new BlobRef { Value = $"c{Guid.NewGuid():N}/nope.bin" };

        (await store.OpenReadAsync(missing, ct)).Should().BeNull();
        (await store.GetMetadataAsync(missing, ct)).Should().BeNull();
        (await store.ExistsAsync(missing, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task CommitAsync_PromotesPending_AndGcLeavesItAlone() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var request = NewRequest() with {
            InitialState = BlobLifecycleState.Pending,
            ExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
        };
        var blobRef = await store.SaveAsync(request, new MemoryStream([1, 2]), ct);

        (await store.CommitAsync(blobRef, ct)).Should().BeTrue();
        // Idempotent: committing an already-committed blob succeeds.
        (await store.CommitAsync(blobRef, ct)).Should().BeTrue();

        // The blob was past its expiry, but the commit cleared it — the collector must leave it alone.
        await store.DeleteExpiredPendingAsync(DateTimeOffset.UtcNow, 100, ct);
        (await store.ExistsAsync(blobRef, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_MissingBlob_ReturnsFalse() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();

        (await store.CommitAsync(new BlobRef { Value = $"c{Guid.NewGuid():N}/nope.bin" }, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteExpiredPendingAsync_ReapsOnlyExpiredPending() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var container = NewContainerName();

        var expiredPending = await store.SaveAsync(
            NewRequest(container) with {
                InitialState = BlobLifecycleState.Pending,
                ExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
            },
            new MemoryStream([1]),
            ct);
        var freshPending = await store.SaveAsync(
            NewRequest(container) with {
                InitialState = BlobLifecycleState.Pending,
                ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(1),
            },
            new MemoryStream([2]),
            ct);
        var committed = await store.SaveAsync(NewRequest(container), new MemoryStream([3]), ct);

        var deleted = await store.DeleteExpiredPendingAsync(DateTimeOffset.UtcNow, 100, ct);

        deleted.Should().BeGreaterThanOrEqualTo(1);
        (await store.ExistsAsync(expiredPending, ct)).Should().BeFalse();
        (await store.ExistsAsync(freshPending, ct)).Should().BeTrue();
        (await store.ExistsAsync(committed, ct)).Should().BeTrue();
    }

    private AzureBlobStore CreateStore() =>
        new(fixture.Client, NullLogger<AzureBlobStore>.Instance);

    private static string NewContainerName() => $"c{Guid.NewGuid():N}";

    private static BlobUploadRequest NewRequest(string? container = null, string? owner = null) =>
        new() {
            Container = container ?? NewContainerName(),
            Name = $"user-1/{Guid.NewGuid():N}/doc.pdf",
            ContentType = "application/pdf",
            OwnerId = owner,
        };
}
