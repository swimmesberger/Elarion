using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.EntityFrameworkCore;

/// <summary>
/// Pins, against a real PostgreSQL instance, the EF Core behavior that motivates the generated
/// client-assigned-Guid-keys pass (and the entity-identity doctrine): the natural "replace the children"
/// update — <c>parent.Children.Clear()</c> + <c>Add(new Child { Id = … })</c> on a tracked parent —
/// INSERTs correctly when the key is declared <c>ValueGeneratedNever</c>, but under EF's convention claim
/// (<c>ValueGeneratedOnAdd</c>) the set id makes the insert-vs-update heuristic track the new child as
/// Modified, so SaveChanges issues an UPDATE that affects zero rows and throws
/// <see cref="DbUpdateConcurrencyException"/> (dotnet/efcore#35090). The InMemory provider skips the
/// affected-rows check — only a real database catches this — hence a Testcontainers pin. Skips when Docker
/// is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ClientAssignedGuidKeyTests(PostgreSqlClientAssignedKeyFixture fixture)
    : IClassFixture<PostgreSqlClientAssignedKeyFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReplaceChildren_WithClientAssignedKeys_InsertsTheNewChild() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var parentId = Guid.CreateVersion7();
        await using (var setup = fixture.CreateContext()) {
            var parent = new NaturalParent { Id = parentId };
            parent.Children.Add(new NaturalChild { Id = Guid.CreateVersion7() });
            setup.Add(parent);
            await setup.SaveChangesAsync(Ct);
        }

        Guid replacementId;
        await using (var update = fixture.CreateContext()) {
            var parent = await update.Set<NaturalParent>()
                .Include(p => p.Children)
                .SingleAsync(p => p.Id == parentId, Ct);
            parent.Children.Clear();
            replacementId = Guid.CreateVersion7();
            parent.Children.Add(new NaturalChild { Id = replacementId });
            await update.SaveChangesAsync(Ct);
        }

        await using var verify = fixture.CreateContext();
        var children = await verify.Set<NaturalChild>()
            .Where(c => c.ParentId == parentId)
            .ToListAsync(Ct);
        children.Should().ContainSingle(c => c.Id == replacementId);
    }

    [Fact]
    public async Task ReplaceChildren_UnderGeneratedKeyConvention_ThrowsOnSave() {
        // The contrast pin: if EF's heuristic ever stops misreading a set id on a "generated" key, this
        // test fails and the generated pass (and its documentation) should be revisited.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var parentId = Guid.CreateVersion7();
        await using (var setup = fixture.CreateContext()) {
            var parent = new LegacyParent { Id = parentId };
            parent.Children.Add(new LegacyChild { Id = Guid.CreateVersion7() });
            setup.Add(parent); // Add(graph) forces the whole graph Added — creates always work.
            await setup.SaveChangesAsync(Ct);
        }

        await using var update = fixture.CreateContext();
        var tracked = await update.Set<LegacyParent>()
            .Include(p => p.Children)
            .SingleAsync(p => p.Id == parentId, Ct);
        tracked.Children.Clear();
        tracked.Children.Add(new LegacyChild { Id = Guid.CreateVersion7() });

        var save = async () => await update.SaveChangesAsync(Ct);

        await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }
}

/// <summary>
/// Starts a disposable PostgreSQL container for the client-assigned-key pin tests and creates the schema
/// once. When Docker is not available the fixture records a skip reason instead of failing, so the suite
/// still runs (and these tests skip) on machines without Docker.
/// </summary>
public sealed class PostgreSqlClientAssignedKeyFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    /// <summary>Gets a value indicating whether the container started and the schema is ready.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Gets the reason the integration tests are skipped when <see cref="IsAvailable"/> is false.</summary>
    public string SkipReason { get; private set; } = "";

    private string _connectionString = "";

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
        _connectionString = container.GetConnectionString();
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Creates a fresh context bound to the container, so each test owns its own change tracker.</summary>
    public ClientAssignedKeyDbContext CreateContext() {
        return new ClientAssignedKeyDbContext(new DbContextOptionsBuilder<ClientAssignedKeyDbContext>()
            .UseNpgsql(_connectionString)
            .Options);
    }
}

/// <summary>
/// Two shapes of the same aggregate: the "natural" pair declares its Guid keys client-assigned
/// (<c>ValueGeneratedNever</c> — what the generated pass produces), the "legacy" pair leaves EF's
/// convention claim (<c>ValueGeneratedOnAdd</c>) in place to pin the misclassification it causes.
/// </summary>
public sealed class ClientAssignedKeyDbContext(DbContextOptions<ClientAssignedKeyDbContext> options)
    : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<NaturalParent>(b => {
            b.ToTable("natural_parents");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedNever();
            b.HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);
        });
        modelBuilder.Entity<NaturalChild>(b => {
            b.ToTable("natural_children");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<LegacyParent>(b => {
            b.ToTable("legacy_parents");
            b.HasKey(p => p.Id); // Guid PK: EF's convention claims ValueGeneratedOnAdd.
            b.HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);
        });
        modelBuilder.Entity<LegacyChild>(b => {
            b.ToTable("legacy_children");
            b.HasKey(c => c.Id);
        });
    }
}

public sealed class NaturalParent {
    public Guid Id { get; set; }
    public List<NaturalChild> Children { get; } = [];
}

public sealed class NaturalChild {
    public Guid Id { get; set; }
    public Guid ParentId { get; set; }
}

public sealed class LegacyParent {
    public Guid Id { get; set; }
    public List<LegacyChild> Children { get; } = [];
}

public sealed class LegacyChild {
    public Guid Id { get; set; }
    public Guid ParentId { get; set; }
}
