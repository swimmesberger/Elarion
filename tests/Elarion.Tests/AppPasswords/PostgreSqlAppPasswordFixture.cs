using Elarion.AppPasswords.EntityFrameworkCore;
using Elarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.AppPasswords;

/// <summary>
/// Starts a disposable PostgreSQL container for the app-password store tests and creates the schema once. Skips (never
/// fails) when Docker is unavailable, as the device identity fixture does.
/// </summary>
public sealed class PostgreSqlAppPasswordFixture : IAsyncLifetime {
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

    public AppPasswordIntegrationDbContext CreateContext() {
        return new AppPasswordIntegrationDbContext(new DbContextOptionsBuilder<AppPasswordIntegrationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);
    }
}

/// <summary>A context mapping the app-password table the way the generated seam does.</summary>
public sealed class AppPasswordIntegrationDbContext(DbContextOptions<AppPasswordIntegrationDbContext> options)
    : DbContext(options) {
    public DbSet<AppPasswordEntity> ElarionAppPasswords => Set<AppPasswordEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.UseElarionAppPasswords();
    }
}
