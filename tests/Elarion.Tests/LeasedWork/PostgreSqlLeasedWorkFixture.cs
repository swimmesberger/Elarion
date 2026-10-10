using Elarion.EntityFrameworkCore.LeasedWork;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.LeasedWork;

/// <summary>
/// Starts a disposable PostgreSQL container for the leased-work-row integration tests and creates the schema once.
/// Skips (never fails) when Docker is unavailable, mirroring the outbox fixture.
/// </summary>
public sealed class PostgreSqlLeasedWorkFixture : IAsyncLifetime {
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
        if (_container is not null) await _container.DisposeAsync();
    }

    public LeasedWorkIntegrationDbContext CreateContext() {
        return new LeasedWorkIntegrationDbContext(new DbContextOptionsBuilder<LeasedWorkIntegrationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);
    }
}

/// <summary>
/// A queue-shaped table deliberately unlike the outbox: an outbound delivery with a deadline, a terminal state, an
/// attempt counted at claim time, and a producer-side withdraw — the second design input ADR-0073 waited for.
/// </summary>
public sealed class TestDelivery : ILeasedWorkRow {
    public required Guid Id { get; init; }

    /// <summary>Isolates each test's rows inside the shared table.</summary>
    public required string Source { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public string State { get; set; } = TestDeliveryStates.Queued;

    public int Attempts { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public string? Error { get; set; }

    public Guid? LockId { get; set; }

    public DateTimeOffset? LockedUntilUtc { get; set; }
}

public static class TestDeliveryStates {
    public const string Queued = "queued";
    public const string Sent = "sent";
    public const string Withdrawn = "withdrawn";
}

/// <summary>Maps only the test delivery table, with PascalCase columns to cover the non-snake-case mapping.</summary>
public sealed class LeasedWorkIntegrationDbContext(DbContextOptions<LeasedWorkIntegrationDbContext> options)
    : DbContext(options) {
    public DbSet<TestDelivery> Deliveries => Set<TestDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<TestDelivery>(entity => {
            entity.ToTable("TestDeliveries");
            entity.HasKey(delivery => delivery.Id);
            entity.Property(delivery => delivery.Id).ValueGeneratedNever();
            entity.HasElarionLeasedWork(
                delivery => new { delivery.Source, delivery.CreatedAtUtc, delivery.Id },
                "\"CompletedAtUtc\" IS NULL",
                snakeCase: false);
        });
    }
}
