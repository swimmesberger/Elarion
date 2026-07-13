using Elarion.Devices.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Devices;

/// <summary>
/// Starts a disposable PostgreSQL container for the device identity store integration tests and
/// creates the schema once. Skips (never fails) when Docker is unavailable, mirroring the
/// settings/blob/actor-snapshot fixtures.
/// </summary>
public sealed class PostgreSqlDeviceIdentityFixture : IAsyncLifetime {
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

    public DeviceIdentityIntegrationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DeviceIdentityIntegrationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);
}

/// <summary>Context mapping the device identity tables the way a generated context would
/// (the seam calls <c>UseElarionDeviceIdentity</c>).</summary>
public sealed class DeviceIdentityIntegrationDbContext(DbContextOptions<DeviceIdentityIntegrationDbContext> options)
    : DbContext(options) {
    public DbSet<DeviceKeyEntity> DeviceKeys => Set<DeviceKeyEntity>();

    public DbSet<DevicePairingCodeEntity> DevicePairingCodes => Set<DevicePairingCodeEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.UseElarionDeviceIdentity();
}
