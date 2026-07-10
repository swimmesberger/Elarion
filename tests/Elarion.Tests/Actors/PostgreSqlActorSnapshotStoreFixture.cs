using Elarion.Actors.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Actors;

/// <summary>
/// Starts a disposable PostgreSQL container for the actor snapshot store integration tests and
/// creates the schema once. Skips (never fails) when Docker is unavailable, mirroring the
/// settings/blob/idempotency fixtures.
/// </summary>
public sealed class PostgreSqlActorSnapshotStoreFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = "";

    public string ConnectionString { get; private set; } = "";

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
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) {
            await _container.DisposeAsync();
        }
    }

    public ActorSnapshotIntegrationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ActorSnapshotIntegrationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);
}

/// <summary>Context mapping the snapshot table the way a generated context would (the seam calls
/// <c>UseElarionActorSnapshots</c>).</summary>
public sealed class ActorSnapshotIntegrationDbContext(DbContextOptions<ActorSnapshotIntegrationDbContext> options)
    : DbContext(options) {
    public DbSet<ActorSnapshotEntity> ActorSnapshots => Set<ActorSnapshotEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.UseElarionActorSnapshots();
}
