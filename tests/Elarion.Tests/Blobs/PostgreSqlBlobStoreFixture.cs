using Elarion.Blobs.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Starts a disposable PostgreSQL container for the blob-store integration tests and creates the
/// blob schema once. When Docker is not available the fixture records a skip reason instead of
/// failing, so the suite still runs (and these tests skip) on machines without Docker.
/// </summary>
public sealed class PostgreSqlBlobStoreFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    /// <summary>Gets a value indicating whether the container started and the schema is ready.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Gets the reason the integration tests are skipped when <see cref="IsAvailable"/> is false.</summary>
    public string SkipReason { get; private set; } = "";

    /// <summary>Gets the container connection string, for tests that open their own data sources.</summary>
    public string ConnectionString { get; private set; } = "";

    /// <summary>Gets the shared data source the store draws streaming-read connections from.</summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync() {
        PostgreSqlContainer container;
        try {
            // Build() validates the Docker endpoint, so it must run inside the guard too.
            container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
        }
        catch (Exception ex) {
            // The only expected failure here is Docker being unavailable; surface it as a skip.
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

    /// <summary>Creates a fresh context bound to the container, so each test owns its own connection.</summary>
    public IntegrationBlobDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<IntegrationBlobDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);
}

/// <summary>EF Core context mapping the PostgreSQL blob tables for integration tests.</summary>
public sealed class IntegrationBlobDbContext(DbContextOptions<IntegrationBlobDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.UseElarionBlobStorage();
}
