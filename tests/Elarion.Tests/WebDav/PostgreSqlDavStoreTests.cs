using System.Text;
using AwesomeAssertions;
using Elarion.Abstractions.Identity;
using Elarion.Tests.Infrastructure;
using Elarion.WebDav;
using Elarion.WebDav.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.WebDav;

/// <summary>
/// Starts a disposable PostgreSQL container for the DAV store tests and creates the schema once. Skips (never fails) when
/// Docker is unavailable, as the app-password fixture does.
/// </summary>
public sealed class PostgreSqlDavStoreFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = "";

    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync() {
        PostgreSqlContainer container;
        try {
            container = await TestContainerLifecycle.Shared.StartAsync(() => new PostgreSqlBuilder("postgres:17-alpine").Build());
        }
        catch (Exception ex) {
            SkipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
            return;
        }

        _container = container;
        ConnectionString = container.GetConnectionString();
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() => await TestContainerLifecycle.Shared.DisposeAsync(_container);

    public DavStoreIntegrationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DavStoreIntegrationDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>A provider with the EF store over the fixture's database, the test protocol and a scoped test user.</summary>
    public ServiceProvider CreateServices(TimeProvider time, Action<WebDavOptions>? options = null, Action<IServiceCollection>? configure = null) {
        var services = new ServiceCollection();
        services.AddSingleton(time);
        services.AddDbContext<DavStoreIntegrationDbContext>(o => o.UseNpgsql(ConnectionString));
        services.AddElarionWebDavEntityFrameworkCore<DavStoreIntegrationDbContext>(configure: options);
        services.AddSingleton<IDavProtocol, TestThingsProtocol>();
        services.AddScoped<TestUserAccessor>();
        services.AddScoped<ICurrentUser>(p => p.GetRequiredService<TestUserAccessor>().User);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

/// <summary>A context mapping the DAV store the way the generated seam does.</summary>
public sealed class DavStoreIntegrationDbContext(DbContextOptions<DavStoreIntegrationDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.UseElarionDavStore();
}

/// <summary>The store contract on the EF store over real PostgreSQL.</summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlDavStoreContractTests(PostgreSqlDavStoreFixture fixture)
    : DavStoreContractTests, IClassFixture<PostgreSqlDavStoreFixture> {
    protected override ServiceProvider CreateServices(FakeTimeProvider time, Action<WebDavOptions>? options = null) =>
        fixture.CreateServices(time, options);

    protected override void SkipUnlessAvailable() => Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
}

/// <summary>What only a real database shows: transactions, row locks and the published table names.</summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlDavStoreTests(PostgreSqlDavStoreFixture fixture) : IClassFixture<PostgreSqlDavStoreFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DavObjectContent Content(string uid, string text) {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new DavObjectContent { Name = uid + ".ics", Uid = uid, Content = bytes, ContentType = "text/plain", ETag = DavETag.Compute(bytes) };
    }

    [Fact]
    public async Task APublishInTheHandlersTransaction_IsVisibleWhenItCommits_AndLeavesNothingWhenItRollsBack() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var services = fixture.CreateServices(TimeProvider.System);
        var address = new DavCollectionAddress("thing", "tx" + Guid.NewGuid().ToString("N")[..8], "k");

        await using (var scope = services.CreateAsyncScope()) {
            var context = scope.ServiceProvider.GetRequiredService<DavStoreIntegrationDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<IDavCollectionStore>().PublishAsync(address, Content("a", "A"), Ct);
            await using (var outside = services.CreateAsyncScope())
                (await outside.ServiceProvider.GetRequiredService<IDavCollectionStore>().GetStatesAsync([address], Ct))[0].SyncSeq
                    .Should().Be(0, "an uncommitted publish is invisible to other connections");
            await transaction.RollbackAsync(Ct);
        }

        await using (var scope = services.CreateAsyncScope()) {
            var context = scope.ServiceProvider.GetRequiredService<DavStoreIntegrationDbContext>();
            (await context.Set<DavChangeEntity>().CountAsync(c => c.CollectionId == address.Id, Ct)).Should().Be(0);
            (await context.Set<DavObjectEntity>().CountAsync(o => o.CollectionId == address.Id, Ct)).Should().Be(0);

            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<IDavCollectionStore>().PublishAsync(address, Content("a", "A"), Ct);
            await transaction.CommitAsync(Ct);
        }

        await using (var scope = services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<IDavCollectionStore>().GetStatesAsync([address], Ct))[0].SyncSeq.Should().Be(1);
    }

    [Fact]
    public async Task TwoPublishersOfOneCalendarInParallelTransactions_GetStrictlyIncreasingTokensAndLoseNoChange() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var services = fixture.CreateServices(TimeProvider.System);
        var address = new DavCollectionAddress("thing", "par" + Guid.NewGuid().ToString("N")[..8], "k");
        const int perWriter = 25;

        async Task WriterAsync(string prefix) {
            for (var i = 0; i < perWriter; i++) {
                await using var scope = services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<DavStoreIntegrationDbContext>();
                await using var transaction = await context.Database.BeginTransactionAsync(Ct);
                await scope.ServiceProvider.GetRequiredService<IDavCollectionStore>().PublishAsync(address, Content(prefix + i, "x"), Ct);
                await transaction.CommitAsync(Ct);
            }
        }

        await Task.WhenAll(Task.Run(() => WriterAsync("a"), Ct), Task.Run(() => WriterAsync("b"), Ct));

        await using var check = services.CreateAsyncScope();
        var store = check.ServiceProvider.GetRequiredService<IDavCollectionStore>();
        var changes = await store.GetChangesAsync(address.Id, 0, Ct);
        changes!.CurrentSeq.Should().Be(2 * perWriter);
        changes.Changes.Select(static c => c.Seq).Should().Equal(Enumerable.Range(1, 2 * perWriter).Select(static i => (long)i));
        changes.Changes.Select(static c => c.Name).Distinct().Should().HaveCount(2 * perWriter);
        (await store.ListMembersAsync(address.Id, Ct).CountAsync(Ct)).Should().Be(2 * perWriter);
    }

    [Fact]
    public async Task SyncingAnUnchangedCalendar_WritesNothing() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var services = fixture.CreateServices(TimeProvider.System);
        var address = new DavCollectionAddress("thing", "ro" + Guid.NewGuid().ToString("N")[..8], "k");
        var protocol = (TestThingsProtocol)services.GetRequiredService<IDavProtocol>();
        protocol.Show("u1", address.Source, address.Key);
        await using (var scope = services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IDavCollectionStore>().PublishAsync(address, Content("a", "A"), Ct);
        var before = await WritesAsync();

        var dispatcher = services.GetRequiredService<DavDispatcher>();
        for (var i = 0; i < 20; i++) {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TestUserAccessor>().User = new TestDavUser("u1");
            var token = DavSyncToken.Format(address.Id, 1);
            var response = await dispatcher.HandleAsync(new DavRequest {
                Method = "REPORT", Path = "/things/u1/" + address.Segment + "/", HrefBase = "/dav",
                Body = new MemoryStream(Encoding.UTF8.GetBytes(DavTestHarness.SyncCollection(token))),
            }, scope.ServiceProvider, Ct);
            await response.WriteBodyAsync(Stream.Null, Ct);
            response.StatusCode.Should().Be(207);
        }

        (await WritesAsync()).Should().Be(before);

        // Row versions (xmin) change with every write; the journal only grows by inserts.
        async Task<string> WritesAsync() {
            await using var context = fixture.CreateContext();
            var id = address.Id;
            var versions = await context.Database.SqlQuery<string>(
                    $"SELECT string_agg(xmin::text, ',') AS \"Value\" FROM (SELECT xmin FROM elarion_dav_collections WHERE id = {id} UNION ALL SELECT xmin FROM elarion_dav_objects WHERE collection_id = {id}) v")
                .SingleAsync(Ct);
            var journal = await context.Set<DavChangeEntity>().CountAsync(c => c.CollectionId == id, Ct);
            return versions + "|" + journal;
        }
    }

    [Fact]
    public async Task TheTables_HaveThePublishedNamesAndKeys() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();

        var tables = await context.Database.SqlQueryRaw<string>(
                "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name LIKE 'elarion_dav_%' ORDER BY table_name")
            .ToListAsync(Ct);
        var objectColumns = await context.Database.SqlQueryRaw<string>(
                "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'elarion_dav_objects' ORDER BY column_name")
            .ToListAsync(Ct);
        var uniqueIndexes = await context.Database.SqlQueryRaw<string>(
                "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename LIKE 'elarion_dav_%' AND indexdef LIKE 'CREATE UNIQUE INDEX%'")
            .ToListAsync(Ct);

        tables.Should().Equal("elarion_dav_changes", "elarion_dav_collections", "elarion_dav_objects");
        objectColumns.Should().Equal("collection_id", "content", "content_type", "etag", "name", "range_end", "range_start", "size", "uid");
        uniqueIndexes.Should().Contain(i => i.Contains("(kind, source, key)")).And.Contain(i => i.Contains("(collection_id, uid)"));
    }
}
