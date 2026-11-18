using Elarion.Actors.PostgreSql;
using Elarion.Coordination.PostgreSql;
using Elarion.Tests.Infrastructure;
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
            container = await TestContainerLifecycle.Shared.StartAsync(() =>
                new PostgreSqlBuilder("postgres:17-alpine").Build());
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
        await TestContainerLifecycle.Shared.DisposeAsync(_container);
    }

    public ActorSnapshotIntegrationDbContext CreateContext() {
        return new ActorSnapshotIntegrationDbContext(new DbContextOptionsBuilder<ActorSnapshotIntegrationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);
    }
}

/// <summary>Context mapping the snapshot and role-lease tables the way a generated context would
/// (the seams call <c>UseElarionActorSnapshots</c>/<c>UseElarionRoleLeases</c>).</summary>
public sealed class ActorSnapshotIntegrationDbContext(DbContextOptions<ActorSnapshotIntegrationDbContext> options)
    : DbContext(options) {
    public DbSet<ActorSnapshotEntity> ActorSnapshots => Set<ActorSnapshotEntity>();

    public DbSet<RoleLeaseEntity> RoleLeases => Set<RoleLeaseEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.UseElarionActorSnapshots();
        modelBuilder.UseElarionRoleLeases();
    }
}
