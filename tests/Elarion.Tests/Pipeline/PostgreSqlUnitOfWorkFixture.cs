using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Pipeline;

/// <summary>
/// Starts a disposable PostgreSQL container for the EF Core unit-of-work integration tests and creates the
/// schema once. Skips (never fails) when Docker is unavailable, mirroring the idempotency/settings fixtures.
/// </summary>
public sealed class PostgreSqlUnitOfWorkFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = "";

    private string ConnectionString { get; set; } = "";

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

    public UnitOfWorkDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<UnitOfWorkDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);
}

/// <summary>Context mapping a couple of demo tables so unit-of-work tests can prove writes commit/roll back.</summary>
public sealed class UnitOfWorkDbContext(DbContextOptions<UnitOfWorkDbContext> options) : DbContext(options) {
    public DbSet<WidgetRow> Widgets => Set<WidgetRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<WidgetRow>(builder => {
            builder.ToTable("widgets");
            builder.HasKey(row => row.Id);
            builder.Property(row => row.Id).HasColumnName("id");
            builder.Property(row => row.Name).HasColumnName("name");
        });
    }
}

/// <summary>A demo row written inside a unit of work.</summary>
public sealed class WidgetRow {
    public required string Id { get; init; }

    public required string Name { get; init; }
}
