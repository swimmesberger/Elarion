using Elarion.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// Starts a disposable PostgreSQL container for the EF Core settings-store integration tests and creates the
/// settings schema once. When Docker is not available the fixture records a skip reason instead of failing,
/// so the suite still runs (and these tests skip) on machines without Docker.
/// </summary>
public sealed class PostgreSqlSettingsStoreFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    /// <summary>Gets a value indicating whether the container started and the schema is ready.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Gets the reason the integration tests are skipped when <see cref="IsAvailable"/> is false.</summary>
    public string SkipReason { get; private set; } = "";

    /// <summary>Gets the container connection string, for tests that open their own connections.</summary>
    public string ConnectionString { get; private set; } = "";

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
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates a fresh context bound to the container, so each test owns its own connection.</summary>
    public SettingsIntegrationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SettingsIntegrationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);
}

/// <summary>EF Core context mapping the Elarion settings table for integration tests.</summary>
public sealed class SettingsIntegrationDbContext(DbContextOptions<SettingsIntegrationDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.UseElarionSettings();
}
