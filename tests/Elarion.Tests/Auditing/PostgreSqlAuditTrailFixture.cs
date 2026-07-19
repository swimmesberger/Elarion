using Elarion.Abstractions.Auditing;
using Elarion.Auditing.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Auditing;

/// <summary>
/// Starts a disposable PostgreSQL container for the EF Core audit-trail integration tests and creates the
/// schema once. Skips (never fails) when Docker is unavailable, mirroring the idempotency/settings fixtures.
/// </summary>
public sealed class PostgreSqlAuditTrailFixture : IAsyncLifetime {
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
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>A plain context for seeding/verification — no interceptors, no audit scope.</summary>
    public AuditIntegrationDbContext CreateContext() {
        return new AuditIntegrationDbContext(new DbContextOptionsBuilder<AuditIntegrationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);
    }
}

/// <summary>Context mapping the audit-log table plus an <c>[Audited]</c> business entity, so tests can prove the
/// success record commits atomically with the business write and the change capture diffs opted-in columns.</summary>
public sealed class AuditIntegrationDbContext(DbContextOptions<AuditIntegrationDbContext> options)
    : DbContext(options) {
    public DbSet<AuditedProperty> Properties => Set<AuditedProperty>();

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<AuditedProperty>(builder => {
            builder.ToTable("properties");
            builder.HasKey(property => property.Id);
            builder.Property(property => property.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(property => property.Name).HasColumnName("name");
            builder.Property(property => property.Street).HasColumnName("street");
            builder.Property(property => property.Secret).HasColumnName("secret");
        });

        modelBuilder.UseElarionAuditing();
    }
}

/// <summary>An opted-in entity with one excluded column, exercising the capture policy edges.</summary>
[Audited]
public sealed class AuditedProperty {
    public required Guid Id { get; init; }

    public required string Name { get; set; }

    public required string Street { get; set; }

    /// <summary>Never captured — the <c>[AuditIgnore]</c> assertion target.</summary>
    [AuditIgnore]
    public string Secret { get; set; } = "";
}
