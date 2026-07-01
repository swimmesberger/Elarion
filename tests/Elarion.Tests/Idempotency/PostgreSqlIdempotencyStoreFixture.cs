using Elarion.Idempotency.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Idempotency;

/// <summary>
/// Starts a disposable PostgreSQL container for the EF Core idempotency-store integration tests and creates the
/// schema once. Skips (never fails) when Docker is unavailable, mirroring the settings/blob fixtures.
/// </summary>
public sealed class PostgreSqlIdempotencyStoreFixture : IAsyncLifetime {
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

    public IdempotencyIntegrationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<IdempotencyIntegrationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);
}

/// <summary>Context mapping the idempotency table plus a demo business entity, so tests can prove the business
/// writes commit atomically with the key row (and roll back for a losing duplicate).</summary>
public sealed class IdempotencyIntegrationDbContext(DbContextOptions<IdempotencyIntegrationDbContext> options)
    : DbContext(options) {
    public DbSet<DemoRow> DemoRows => Set<DemoRow>();

    public DbSet<IdempotencyKeyEntity> IdempotencyKeys => Set<IdempotencyKeyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<DemoRow>(builder => {
            builder.ToTable("demo_rows");
            builder.HasKey(row => row.Id);
            builder.Property(row => row.Id).HasColumnName("id");
            builder.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key");
        });

        modelBuilder.ApplyElarionIdempotencyKeys();
    }
}

/// <summary>A business row a demo handler writes, tagged with the idempotency key that produced it.</summary>
public sealed class DemoRow {
    public required string Id { get; init; }

    public required string IdempotencyKey { get; init; }
}
