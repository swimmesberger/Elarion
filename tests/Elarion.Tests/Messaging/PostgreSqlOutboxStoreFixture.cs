using Elarion.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Messaging;

/// <summary>
/// Starts a disposable PostgreSQL container for the EF Core outbox-store integration tests and creates the schema
/// once. Skips (never fails) when Docker is unavailable, mirroring the settings/idempotency/blob fixtures.
/// </summary>
public sealed class PostgreSqlOutboxStoreFixture : IAsyncLifetime {
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

    public OutboxIntegrationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<OutboxIntegrationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);
}

/// <summary>Context mapping only the outbox table for the store integration tests.</summary>
public sealed class OutboxIntegrationDbContext(DbContextOptions<OutboxIntegrationDbContext> options)
    : DbContext(options) {
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<OutboxDelivery> OutboxDeliveries => Set<OutboxDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.UseElarionOutbox();
}
