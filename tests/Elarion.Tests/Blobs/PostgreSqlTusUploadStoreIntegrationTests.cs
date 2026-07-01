using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.PostgreSql;
using Elarion.Blobs.Tus;
using Elarion.Blobs.Tus.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Integration tests for the durable PostgreSQL tus staging store against a real PostgreSQL instance:
/// staged bytes accumulate across appends and finalize into a pending blob; offset is enforced; expired
/// incomplete sessions are reclaimed. Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlTusUploadStoreIntegrationTests(PostgreSqlTusFixture fixture)
    : IClassFixture<PostgreSqlTusFixture> {
    [Fact]
    public async Task CreateAppendComplete_ProducesPendingBlobWithContent() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out var blobStore);
        var payload = new byte[] { 10, 20, 30, 40, 50 };

        var created = await store.CreateAsync(NewCreation(payload.Length), ct);
        created.Offset.Should().Be(0);

        var completed = await store.AppendAsync(created.Id, 0, new MemoryStream(payload), ct);

        completed.Offset.Should().Be(5);
        completed.BlobRef.Should().NotBeNull();
        (await blobStore.ReadAllBytesAsync(completed.BlobRef!.Value, ct)).Should().Equal(payload);

        await using var verify = fixture.CreateContext();
        var blobId = completed.BlobRef!.Value.Value;
        (await verify.Set<StoredBlob>().AsNoTracking().FirstAsync(b => b.Id == blobId, ct))
            .State.Should().Be(BlobLifecycleState.Pending);
    }

    [Fact]
    public async Task ResumableAppend_TwoChunks_Completes() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out var blobStore);

        var created = await store.CreateAsync(NewCreation(6), ct);
        var afterFirst = await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3]), ct);
        afterFirst.Offset.Should().Be(3);
        afterFirst.BlobRef.Should().BeNull();

        (await store.GetAsync(created.Id, ct))!.Offset.Should().Be(3);

        var completed = await store.AppendAsync(created.Id, 3, new MemoryStream([4, 5, 6]), ct);
        completed.BlobRef.Should().NotBeNull();
        (await blobStore.ReadAllBytesAsync(completed.BlobRef!.Value, ct)).Should().Equal([1, 2, 3, 4, 5, 6]);
    }

    [Fact]
    public async Task AppendAsync_ZeroLength_FinalizesToEmptyBlob() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out var blobStore);

        var created = await store.CreateAsync(NewCreation(0), ct);
        var completed = await store.AppendAsync(created.Id, 0, Stream.Null, ct);

        completed.BlobRef.Should().NotBeNull();
        (await blobStore.ReadAllBytesAsync(completed.BlobRef!.Value, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_WrongOffset_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var created = await store.CreateAsync(NewCreation(4), ct);

        var act = async () => await store.AppendAsync(created.Id, 2, new MemoryStream([1, 2]), ct);

        await act.Should().ThrowAsync<TusOffsetConflictException>();
    }

    [Fact]
    public async Task DeleteExpiredAsync_RemovesIncompleteOnly() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        // Negative upload expiry makes new sessions immediately eligible for reclamation.
        var store = CreateStore(context, out _, options => options.UploadExpiry = TimeSpan.FromMinutes(-1));

        var incomplete = await store.CreateAsync(NewCreation(8), ct);
        var willComplete = await store.CreateAsync(NewCreation(2), ct);
        var completed = await store.AppendAsync(willComplete.Id, 0, new MemoryStream([1, 2]), ct);
        completed.BlobRef.Should().NotBeNull();

        var deleted = await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, 100, ct);

        deleted.Should().Be(1);
        (await store.GetAsync(incomplete.Id, ct)).Should().BeNull();
        (await store.GetAsync(willComplete.Id, ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Finalize_StampsBlobIdAndCompletedRetentionAtomically() {
        // Regression (M11): finalization writes the pending blob and stamps the session's blob_id in one
        // transaction, so a completed session (offset == length) always carries its blob reference, and
        // (M10) an eligibility expiry so it can later be reaped.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _, configureGc: gc => gc.CompletedRetention = TimeSpan.FromHours(2));
        var created = await store.CreateAsync(NewCreation(3), ct);
        var before = DateTimeOffset.UtcNow;

        var completed = await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3]), ct);
        completed.BlobRef.Should().NotBeNull();

        await using var verify = fixture.CreateContext();
        var row = await verify.Set<TusUploadRow>().AsNoTracking().FirstAsync(r => r.Id == created.Id, ct);
        row.BlobId.Should().Be(completed.BlobRef!.Value.Value);
        row.Data.Should().BeEmpty();
        // The completed session's expiry is stamped to the completed-retention window, not the upload expiry.
        row.ExpiresAt.Should().BeCloseTo(before + TimeSpan.FromHours(2), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ReapsCompletedSessionPastRetention() {
        // Regression (M10): completed session rows are garbage-collected once past their retention window,
        // so the staging table does not grow unbounded.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        // A negative completed retention makes a freshly completed session immediately eligible.
        var store = CreateStore(context, out _, configureGc: gc => gc.CompletedRetention = TimeSpan.FromMinutes(-1));

        var willComplete = await store.CreateAsync(NewCreation(2), ct);
        var completed = await store.AppendAsync(willComplete.Id, 0, new MemoryStream([1, 2]), ct);
        completed.BlobRef.Should().NotBeNull();

        var deleted = await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, 100, ct);

        deleted.Should().Be(1);
        (await store.GetAsync(willComplete.Id, ct)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteExpiredAsync_KeepsCompletedSessionWithinRetention() {
        // A completed session must live long enough for a HEAD to fetch the reference.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _, configureGc: gc => gc.CompletedRetention = TimeSpan.FromHours(1));

        var willComplete = await store.CreateAsync(NewCreation(2), ct);
        var completed = await store.AppendAsync(willComplete.Id, 0, new MemoryStream([1, 2]), ct);
        completed.BlobRef.Should().NotBeNull();

        var deleted = await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, 100, ct);

        deleted.Should().Be(0);
        var stillThere = await store.GetAsync(willComplete.Id, ct);
        stillThere.Should().NotBeNull();
        stillThere!.BlobRef.Should().NotBeNull();
    }

    [Fact]
    public async Task Finalize_RecordsOwnerIdOnBlob() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out var blobStore);
        var created = await store.CreateAsync(NewCreation(2), ct);

        var completed = await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2]), ct);

        (await blobStore.GetMetadataAsync(completed.BlobRef!.Value, ct))!.OwnerId.Should().Be("user-1");
    }

    private PostgreSqlTusUploadStore<TusBlobDbContext> CreateStore(
        TusBlobDbContext context,
        out PostgreSqlBlobStore<TusBlobDbContext> blobStore,
        Action<TusOptions>? configure = null,
        Action<TusGcOptions>? configureGc = null) {
        blobStore = new PostgreSqlBlobStore<TusBlobDbContext>(
            context, fixture.DataSource, NullLogger<PostgreSqlBlobStore<TusBlobDbContext>>.Instance, TimeProvider.System);
        var options = new TusOptions();
        configure?.Invoke(options);
        var gcOptions = new TusGcOptions();
        configureGc?.Invoke(gcOptions);
        return new PostgreSqlTusUploadStore<TusBlobDbContext>(
            context, blobStore, options, gcOptions, TimeProvider.System);
    }

    private static TusUploadCreation NewCreation(long length) =>
        new() {
            Container = $"c-{Guid.NewGuid():N}",
            Name = $"user-1/{Guid.NewGuid():N}/file.bin",
            Length = length,
            ContentType = "application/octet-stream",
            OwnerId = "user-1"
        };
}

/// <summary>
/// Starts a disposable PostgreSQL container and creates a schema with both the blob tables and the tus
/// staging table, so the tus store can finalize uploads into blobs. Skips when Docker is unavailable.
/// </summary>
public sealed class PostgreSqlTusFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = "";

    private string ConnectionString { get; set; } = "";

    /// <summary>Gets the shared data source the blob store draws streaming-read connections from.</summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync() {
        PostgreSqlContainer container;
        try {
            container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
        }
        catch (Exception ex) {
            SkipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
            return;
        }

        _container = container;
        ConnectionString = container.GetConnectionString();
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (DataSource is not null) {
            await DataSource.DisposeAsync();
        }

        if (_container is not null) {
            await _container.DisposeAsync();
        }
    }

    public TusBlobDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TusBlobDbContext>().UseNpgsql(ConnectionString).Options);
}

/// <summary>EF Core context mapping both the blob tables and the tus staging table.</summary>
public sealed class TusBlobDbContext(DbContextOptions<TusBlobDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.UseElarionBlobStorage();
        modelBuilder.UseElarionTusStorage();
    }
}
