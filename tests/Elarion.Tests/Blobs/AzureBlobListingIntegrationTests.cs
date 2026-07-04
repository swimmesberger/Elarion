using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Integration tests for <see cref="AzureBlobStore"/> listing against Azurite: flat prefix listing with
/// service continuation tokens, native delimiter roll-up into virtual directories, lifecycle-state
/// filtering (client-side per page — the documented Azure delta), and container enumeration. Skips when
/// Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AzureBlobListingIntegrationTests(AzuriteFixture fixture) : IClassFixture<AzuriteFixture> {
    [Fact]
    public async Task ListAsync_Flat_PagesInOrderWithPrefix() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var container = await SeedTreeAsync(store, ct);

        var first = await store.ListAsync(
            new BlobListRequest { Container = container, Prefix = "docs/", PageSize = 2 }, ct);

        first.Blobs.Select(b => b.Name).Should().Equal("docs/1.txt", "docs/2.txt");
        first.Prefixes.Should().BeEmpty();
        first.Blobs.Should().OnlyContain(b => b.State == BlobLifecycleState.Committed && b.Container == container);
        first.ContinuationToken.Should().NotBeNull();

        var second = await store.ListAsync(
            new BlobListRequest {
                Container = container, Prefix = "docs/", PageSize = 2, ContinuationToken = first.ContinuationToken,
            },
            ct);

        second.Blobs.Select(b => b.Name).Should().Equal("docs/sub/3.txt");
    }

    [Fact]
    public async Task ListAsync_WithDelimiter_RollsUpVirtualDirectories() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var container = await SeedTreeAsync(store, ct);

        var root = await store.ListAsync(
            new BlobListRequest { Container = container, Delimiter = "/" }, ct);
        root.Blobs.Select(b => b.Name).Should().Equal("a.txt", "z.txt");
        root.Prefixes.Should().Equal("docs/");

        var docs = await store.ListAsync(
            new BlobListRequest { Container = container, Prefix = "docs/", Delimiter = "/" }, ct);
        docs.Blobs.Select(b => b.Name).Should().Equal("docs/1.txt", "docs/2.txt");
        docs.Prefixes.Should().Equal("docs/sub/");
    }

    [Fact]
    public async Task ListAsync_ListedMetadata_ResolvesThroughTheStore() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var container = $"c{Guid.NewGuid():N}";
        await store.SaveAsync(
            new BlobUploadRequest {
                Container = container, Name = "doc.pdf", ContentType = "application/pdf", OwnerId = "user-1",
            },
            new MemoryStream([1, 2, 3]),
            ct);

        var listing = await store.ListAsync(new BlobListRequest { Container = container }, ct);

        var entry = listing.Blobs.Should().ContainSingle().Subject;
        entry.ContentType.Should().Be("application/pdf");
        entry.Size.Should().Be(3);
        entry.OwnerId.Should().Be("user-1");
        // The listed Id is a resolvable blob reference.
        (await store.ReadAllBytesAsync(new BlobRef { Value = entry.Id }, ct)).Should().Equal([1, 2, 3]);
    }

    [Fact]
    public async Task ListAsync_StateFilter_SelectsLifecycleState() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var container = $"c{Guid.NewGuid():N}";
        await store.SaveAsync(
            new BlobUploadRequest { Container = container, Name = "kept.bin", ContentType = "application/octet-stream" },
            new MemoryStream([1]),
            ct);
        await store.SaveAsync(
            new BlobUploadRequest {
                Container = container,
                Name = "staged.bin",
                ContentType = "application/octet-stream",
                InitialState = BlobLifecycleState.Pending,
                ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(1),
            },
            new MemoryStream([2]),
            ct);

        var committed = await store.ListAsync(
            new BlobListRequest { Container = container, State = BlobLifecycleState.Committed }, ct);
        committed.Blobs.Select(b => b.Name).Should().Equal("kept.bin");

        var pending = await store.ListAsync(
            new BlobListRequest { Container = container, State = BlobLifecycleState.Pending }, ct);
        pending.Blobs.Select(b => b.Name).Should().Equal("staged.bin");
        pending.Blobs[0].State.Should().Be(BlobLifecycleState.Pending);
    }

    [Fact]
    public async Task ListAsync_MissingContainer_ReturnsEmpty() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();

        var listing = await store.ListAsync(
            new BlobListRequest { Container = $"c{Guid.NewGuid():N}" }, ct);

        listing.Blobs.Should().BeEmpty();
        listing.Prefixes.Should().BeEmpty();
        listing.ContinuationToken.Should().BeNull();
    }

    [Fact]
    public async Task ListContainersAsync_IncludesSeededContainer() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var container = $"c{Guid.NewGuid():N}";
        await store.SaveAsync(
            new BlobUploadRequest { Container = container, Name = "a.bin", ContentType = "application/octet-stream" },
            new MemoryStream([1]),
            ct);

        (await store.ListContainersAsync(ct)).Should().Contain(container);
    }

    private static async Task<string> SeedTreeAsync(AzureBlobStore store, CancellationToken ct) {
        var container = $"c{Guid.NewGuid():N}";
        foreach (var name in new[] { "a.txt", "docs/1.txt", "docs/2.txt", "docs/sub/3.txt", "z.txt" }) {
            await store.SaveAsync(
                new BlobUploadRequest { Container = container, Name = name, ContentType = "application/octet-stream" },
                new MemoryStream([1]),
                ct);
        }

        return container;
    }

    private AzureBlobStore CreateStore() => new(fixture.Client, NullLogger<AzureBlobStore>.Instance);
}
