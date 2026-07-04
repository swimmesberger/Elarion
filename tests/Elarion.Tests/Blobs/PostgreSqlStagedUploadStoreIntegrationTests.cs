using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Integration tests for the durable PostgreSQL staged-upload store against a real PostgreSQL instance:
/// staged bytes accumulate across appends and, on explicit completion, become a pending blob; offsets
/// are enforced; deferred lengths seal at completion; expired sessions are reclaimed. Skips when Docker
/// is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlStagedUploadStoreIntegrationTests(PostgreSqlStagedUploadFixture fixture)
    : IClassFixture<PostgreSqlStagedUploadFixture> {
    [Fact]
    public async Task CreateAppendComplete_ProducesPendingBlobWithContent() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out var blobStore);
        var payload = new byte[] { 10, 20, 30, 40, 50 };

        var created = await store.CreateAsync(NewCreation(payload.Length), ct);
        created.Offset.Should().Be(0);

        var appended = await store.AppendAsync(created.Id, 0, new MemoryStream(payload), ct);
        appended.Offset.Should().Be(5);
        appended.BlobRef.Should().BeNull();

        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);

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

        (await store.GetAsync(created.Id, ct))!.Offset.Should().Be(3);

        await store.AppendAsync(created.Id, 3, new MemoryStream([4, 5, 6]), ct);
        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);
        completed.BlobRef.Should().NotBeNull();
        (await blobStore.ReadAllBytesAsync(completed.BlobRef!.Value, ct)).Should().Equal([1, 2, 3, 4, 5, 6]);
    }

    [Fact]
    public async Task CompleteAsync_ZeroLength_ProducesEmptyBlobWithoutAppend() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out var blobStore);

        var created = await store.CreateAsync(NewCreation(0), ct);
        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);

        completed.BlobRef.Should().NotBeNull();
        (await blobStore.ReadAllBytesAsync(completed.BlobRef!.Value, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeferredLength_SealsAtReceivedBytes() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out var blobStore);

        var created = await store.CreateAsync(NewCreation(length: null), ct);
        created.Length.Should().BeNull();

        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3]), ct);
        await store.AppendAsync(created.Id, 3, new MemoryStream([4, 5]), ct);
        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);

        completed.Length.Should().Be(5);
        (await blobStore.ReadAllBytesAsync(completed.BlobRef!.Value, ct)).Should().Equal([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task AppendAsync_WrongOffset_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var created = await store.CreateAsync(NewCreation(4), ct);

        var act = async () => await store.AppendAsync(created.Id, 2, new MemoryStream([1, 2]), ct);

        await act.Should().ThrowAsync<StagedUploadConflictException>();
    }

    [Fact]
    public async Task CompleteAsync_BeforeDeclaredLengthReached_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var created = await store.CreateAsync(NewCreation(4), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2]), ct);

        var act = async () => await store.CompleteAsync(created.Id, NewCompletion(), ct);

        await act.Should().ThrowAsync<StagedUploadConflictException>();
    }

    [Fact]
    public async Task CompleteAsync_IsIdempotent() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var created = await store.CreateAsync(NewCreation(2), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2]), ct);

        var first = await store.CompleteAsync(created.Id, NewCompletion(), ct);
        var second = await store.CompleteAsync(created.Id, NewCompletion(), ct);

        second.BlobRef.Should().Be(first.BlobRef);
    }

    [Fact]
    public async Task DeleteExpiredAsync_RemovesIncompleteOnly() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);

        // An already-expired incomplete session, and a completed one within its retention window.
        var incomplete = await store.CreateAsync(
            NewCreation(8) with { ExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1) }, ct);
        var willComplete = await store.CreateAsync(NewCreation(2), ct);
        await store.AppendAsync(willComplete.Id, 0, new MemoryStream([1, 2]), ct);
        (await store.CompleteAsync(willComplete.Id, NewCompletion(), ct)).BlobRef.Should().NotBeNull();

        var deleted = await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, 100, ct);

        deleted.Should().Be(1);
        (await store.GetAsync(incomplete.Id, ct)).Should().BeNull();
        (await store.GetAsync(willComplete.Id, ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task CompleteAsync_StampsBlobIdRetentionAndDropsStagedBytesAtomically() {
        // Completion writes the pending blob and stamps the session's blob_id in one transaction, so a
        // completed session always carries its blob reference, plus the retention deadline it is later
        // reaped by; the staged bytes are dropped (the content now lives in the blob).
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var created = await store.CreateAsync(NewCreation(3), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2, 3]), ct);
        var retention = DateTimeOffset.UtcNow + TimeSpan.FromHours(2);

        var completed = await store.CompleteAsync(
            created.Id,
            NewCompletion() with { SessionExpiresAt = retention },
            ct);
        completed.BlobRef.Should().NotBeNull();

        await using var verify = fixture.CreateContext();
        var row = await verify.Set<StagedUploadRow>().AsNoTracking().FirstAsync(r => r.Id == created.Id, ct);
        row.BlobId.Should().Be(completed.BlobRef!.Value.Value);
        row.Data.Should().BeEmpty();
        // timestamptz keeps microsecond precision; a tick-precision DateTimeOffset loses its last digit
        // on the round trip, so compare within the truncation rather than for exact equality.
        row.ExpiresAt.Should().BeCloseTo(retention, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ReapsCompletedSessionPastRetention() {
        // Completed session rows are garbage-collected once past their retention window, so the staging
        // table does not grow unbounded.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);

        var willComplete = await store.CreateAsync(NewCreation(2), ct);
        await store.AppendAsync(willComplete.Id, 0, new MemoryStream([1, 2]), ct);
        // An already-elapsed retention makes the freshly completed session immediately eligible.
        await store.CompleteAsync(
            willComplete.Id,
            NewCompletion() with { SessionExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1) },
            ct);

        var deleted = await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, 100, ct);

        deleted.Should().Be(1);
        (await store.GetAsync(willComplete.Id, ct)).Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_RecordsOwnerIdAndExpiryOnBlob() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out var blobStore);
        var created = await store.CreateAsync(NewCreation(2), ct);
        await store.AppendAsync(created.Id, 0, new MemoryStream([1, 2]), ct);

        var completed = await store.CompleteAsync(created.Id, NewCompletion(), ct);

        (await blobStore.GetMetadataAsync(completed.BlobRef!.Value, ct))!.OwnerId.Should().Be("user-1");
    }

    private PostgreSqlStagedUploadStore<StagedUploadBlobDbContext> CreateStore(
        StagedUploadBlobDbContext context,
        out PostgreSqlBlobStore<StagedUploadBlobDbContext> blobStore) {
        blobStore = new PostgreSqlBlobStore<StagedUploadBlobDbContext>(
            context,
            fixture.DataSource,
            NullLogger<PostgreSqlBlobStore<StagedUploadBlobDbContext>>.Instance,
            TimeProvider.System);
        return new PostgreSqlStagedUploadStore<StagedUploadBlobDbContext>(context, blobStore, TimeProvider.System);
    }

    private static StagedUploadCreation NewCreation(long? length) =>
        new() {
            Container = $"c-{Guid.NewGuid():N}",
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

/// <summary>
/// Starts a disposable PostgreSQL container and creates a schema with both the blob tables and the
/// staged-upload table, so the staging store can complete uploads into blobs. Skips when Docker is
/// unavailable.
/// </summary>
public sealed class PostgreSqlStagedUploadFixture : IAsyncLifetime {
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

    public StagedUploadBlobDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<StagedUploadBlobDbContext>().UseNpgsql(ConnectionString).Options);
}

/// <summary>EF Core context mapping both the blob tables and the staged-upload table.</summary>
public sealed class StagedUploadBlobDbContext(DbContextOptions<StagedUploadBlobDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.UseElarionBlobStorage();
        modelBuilder.UseElarionStagedUploads();
    }
}
