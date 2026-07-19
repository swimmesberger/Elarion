using Elarion.BulkOperations.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.BulkOperations;

/// <summary>
/// Docker-gated PostgreSQL fixture for the bulk insert integration tests: starts a real
/// PostgreSQL container when Docker is available and skips (never fails) when not.
/// </summary>
public sealed class PostgreSqlBulkInsertFixture : IAsyncLifetime {
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

    public BulkInsertDbContext CreateContext() {
        return new BulkInsertDbContext(new DbContextOptionsBuilder<BulkInsertDbContext>()
            .UseNpgsql(ConnectionString)
            .UseElarionPostgreSqlBulkOperations()
            .Options);
    }
}

public sealed class BulkInsertDbContext(DbContextOptions<BulkInsertDbContext> options) : DbContext(options) {
    public DbSet<BulkOrder> Orders => Set<BulkOrder>();

    public DbSet<BulkAuditEvent> AuditEvents => Set<BulkAuditEvent>();

    public DbSet<BulkAnimal> Animals => Set<BulkAnimal>();

    public DbSet<BulkShipment> Shipments => Set<BulkShipment>();

    public DbSet<BulkCounter> Counters => Set<BulkCounter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<BulkOrder>(builder => {
            builder.ToTable("bulk_orders");
            builder.HasKey(order => order.Id);
            builder.Property(order => order.Name).HasMaxLength(200);
            builder.Property(order => order.Price).HasPrecision(18, 2);
            builder.Property(order => order.Status).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<BulkAuditEvent>(builder => {
            builder.ToTable("bulk_audit_events");
            builder.HasKey(auditEvent => auditEvent.Id);
            builder.Property(auditEvent => auditEvent.Id).UseIdentityByDefaultColumn();
            builder.Property(auditEvent => auditEvent.RecordedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<BulkShipment>(builder => {
            builder.ToTable("bulk_shipments");
            builder.HasKey(shipment => shipment.Id);
            builder.ComplexProperty(shipment => shipment.Address, address => address.ComplexProperty(a => a.Geo));
        });

        modelBuilder.Entity<BulkCounter>(builder => {
            builder.ToTable("bulk_counters");
            builder.HasKey(counter => counter.Id);
            builder.HasIndex(counter => counter.Key).IsUnique();
        });

        modelBuilder.Entity<BulkAnimal>(builder => {
            builder.ToTable("bulk_animals");
            builder.HasKey(animal => animal.Id);
            builder
                .HasDiscriminator<string>("kind")
                .HasValue<BulkAnimal>("animal")
                .HasValue<BulkDog>("dog")
                .HasValue<BulkCat>("cat");
        });
    }
}

public sealed class BulkOrder {
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public int Quantity { get; set; }

    public decimal Price { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool Active { get; set; }

    public BulkOrderStatus Status { get; set; }

    public int? Rating { get; set; }

    public string[]? Tags { get; set; }

    public byte[]? Payload { get; set; }
}

public enum BulkOrderStatus {
    Draft,
    Submitted,
    Shipped
}

/// <summary>Identity key + store default: both columns must be filled by the database, not the COPY.</summary>
public sealed class BulkAuditEvent {
    public long Id { get; set; }

    public required string Message { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>Complex-property (value object) shapes, including nesting and a nullable inner member.</summary>
public sealed class BulkShipment {
    public Guid Id { get; set; }

    public required string Reference { get; set; }

    public required ShippingAddress Address { get; set; }
}

public sealed class ShippingAddress {
    public required string Street { get; set; }

    public string? Note { get; set; }

    public required GeoPoint Geo { get; set; }
}

public sealed class GeoPoint {
    public double Latitude { get; set; }

    public double Longitude { get; set; }
}

/// <summary>Upsert target: client-assigned PK plus an alternate unique index on <see cref="Key"/>.</summary>
public sealed class BulkCounter {
    public Guid Id { get; set; }

    public required string Key { get; set; }

    public int Count { get; set; }
}

/// <summary>TPH base; intentionally inheritable so derived sets exercise the discriminator path.</summary>
public class BulkAnimal {
    public Guid Id { get; set; }

    public required string Name { get; set; }
}

public sealed class BulkDog : BulkAnimal {
    public string? FavoriteToy { get; set; }
}

public sealed class BulkCat : BulkAnimal {
    public bool Indoor { get; set; }
}
