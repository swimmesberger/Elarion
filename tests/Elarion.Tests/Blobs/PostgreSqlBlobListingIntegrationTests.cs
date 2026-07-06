using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.PostgreSql;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Integration tests for <see cref="PostgreSqlBlobStore{TDbContext}"/> listing against a real PostgreSQL
/// instance: flat prefix listing with keyset pagination in ordinal order, delimiter roll-up into virtual
/// directories, lifecycle-state filtering, and container enumeration. Each test uses a unique container
/// so they stay isolated. Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlBlobListingIntegrationTests(PostgreSqlBlobStoreFixture fixture)
    : IClassFixture<PostgreSqlBlobStoreFixture> {
    [Fact]
    public async Task ListAsync_Flat_PagesInOrdinalOrderWithPrefix() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
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
        second.ContinuationToken.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_WithDelimiter_RollsUpVirtualDirectories() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
        var container = await SeedTreeAsync(store, ct);

        var root = await store.ListAsync(
            new BlobListRequest { Container = container, Delimiter = "/" }, ct);
        root.Blobs.Select(b => b.Name).Should().Equal("a.txt", "z.txt");
        root.Prefixes.Should().Equal("docs/");
        root.ContinuationToken.Should().BeNull();

        var docs = await store.ListAsync(
            new BlobListRequest { Container = container, Prefix = "docs/", Delimiter = "/" }, ct);
        docs.Blobs.Select(b => b.Name).Should().Equal("docs/1.txt", "docs/2.txt");
        docs.Prefixes.Should().Equal("docs/sub/");
    }

    [Fact]
    public async Task ListAsync_HierarchyPagination_WalksMixedEntries() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
        var container = await SeedTreeAsync(store, ct);

        // Root-level entries in ordinal order: "a.txt" (blob), "docs/" (prefix), "z.txt" (blob).
        var first = await store.ListAsync(
            new BlobListRequest { Container = container, Delimiter = "/", PageSize = 2 }, ct);
        first.Blobs.Select(b => b.Name).Should().Equal("a.txt");
        first.Prefixes.Should().Equal("docs/");
        first.ContinuationToken.Should().NotBeNull();

        var second = await store.ListAsync(
            new BlobListRequest {
                Container = container, Delimiter = "/", PageSize = 2, ContinuationToken = first.ContinuationToken,
            },
            ct);
        second.Blobs.Select(b => b.Name).Should().Equal("z.txt");
        second.Prefixes.Should().BeEmpty();
        second.ContinuationToken.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_StateFilter_SelectsLifecycleState() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
        var container = $"c-{Guid.NewGuid():N}";
        await store.SaveAsync(NewRequest(container, "kept.bin"), new MemoryStream([1]), ct);
        await store.SaveAsync(
            NewRequest(container, "staged.bin") with {
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

        var all = await store.ListAsync(new BlobListRequest { Container = container }, ct);
        all.Blobs.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListAsync_MissingContainer_ReturnsEmpty() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);

        var listing = await store.ListAsync(
            new BlobListRequest { Container = $"c-{Guid.NewGuid():N}" }, ct);

        listing.Blobs.Should().BeEmpty();
        listing.Prefixes.Should().BeEmpty();
        listing.ContinuationToken.Should().BeNull();
    }

    [Fact]
    public async Task ListContainersAsync_IncludesSeededContainer() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
        var container = $"c-{Guid.NewGuid():N}";
        await store.SaveAsync(NewRequest(container, "a.bin"), new MemoryStream([1]), ct);

        (await store.ListContainersAsync(ct)).Should().Contain(container);
    }

    private static async Task<string> SeedTreeAsync(
        PostgreSqlBlobStore<IntegrationBlobDbContext> store,
        CancellationToken ct) {
        var container = $"c-{Guid.NewGuid():N}";
        foreach (var name in new[] { "a.txt", "docs/1.txt", "docs/2.txt", "docs/sub/3.txt", "z.txt" }) {
            await store.SaveAsync(NewRequest(container, name), new MemoryStream([1]), ct);
        }

        return container;
    }

    private static BlobUploadRequest NewRequest(string container, string name) =>
        new() {
            Container = container,
            Name = name,
            ContentType = "application/octet-stream",
        };

    private static PostgreSqlBlobStore<IntegrationBlobDbContext> CreateStore(IntegrationBlobDbContext context) =>
        new(context, NullLogger<PostgreSqlBlobStore<IntegrationBlobDbContext>>.Instance, TimeProvider.System);
}
