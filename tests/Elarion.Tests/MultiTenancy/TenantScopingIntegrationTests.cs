using AwesomeAssertions;
using Elarion.Abstractions.MultiTenancy;
using Elarion.EntityFrameworkCore.MultiTenancy;
using Elarion.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.MultiTenancy;

/// <summary>
/// End-to-end verification of ambient tenant scoping against real PostgreSQL: that the read filter is pushed
/// into SQL for a query that says nothing about tenancy, that an insert is stamped without the handler
/// remembering, and that every way of reaching another tenant's row is refused. Skips when Docker is
/// unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantScopingIntegrationTests(TenantScopingFixture fixture)
    : IClassFixture<TenantScopingFixture> {
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task Query_SayingNothingAboutTenancy_OnlyReturnsTheScopedTenantsRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var name = await SeedAcrossTenantsAsync(ct);

        await using var scope = CreateScope(TenantA);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        var notes = await db.Notes.Where(note => note.Name == name).ToListAsync(ct);

        notes.Should().ContainSingle();
        notes[0].TenantId.Should().Be(Guid.Parse(TenantA));
    }

    [Fact]
    public async Task Query_PushesTheTenantPredicateIntoSql() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        await using var scope = CreateScope(TenantA);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        db.Notes.ToQueryString().Should().Contain("tenant_id");
    }

    [Fact]
    public async Task Query_WithNoTenantResolved_ReturnsNothing() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var name = await SeedAcrossTenantsAsync(ct);

        await using var scope = CreateScope(null);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        // Fails closed: an unresolved tenant compares against NULL, so it matches no row — rather than
        // falling back to Guid.Empty and exposing whatever happens to carry it.
        (await db.Notes.Where(note => note.Name == name).ToListAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task SystemScope_SpansEveryTenant() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var name = await SeedAcrossTenantsAsync(ct);

        await using var scope = CreateScope(TenantA);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        using var _ = tenant.SystemScope();
        var notes = await db.Notes.Where(note => note.Name == name).ToListAsync(ct);

        notes.Should().HaveCount(2);
    }

    [Fact]
    public async Task IgnoreQueryFilters_ByKey_DropsOnlyTheTenantFilter() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var name = await SeedAcrossTenantsAsync(ct);

        await using var scope = CreateScope(TenantA);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        var notes = await db.Notes
            .IgnoreQueryFilters([ElarionTenantScoping.QueryFilterKey])
            .Where(note => note.Name == name)
            .ToListAsync(ct);

        notes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Insert_IsStampedWithTheScopedTenant() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var name = $"stamped-{Guid.CreateVersion7()}";
        await using (var scope = CreateScope(TenantB)) {
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            // Note the handler says nothing about the tenant.
            db.Notes.Add(new Note { Name = name });
            await db.SaveChangesAsync(ct);
        }

        await using var reader = CreateScope(TenantB);
        var stored = await reader.ServiceProvider.GetRequiredService<TenantDbContext>()
            .Notes.SingleAsync(note => note.Name == name, ct);

        stored.TenantId.Should().Be(Guid.Parse(TenantB));
    }

    [Fact]
    public async Task Insert_WithNoTenantResolved_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        await using var scope = CreateScope(null);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        db.Notes.Add(new Note { Name = "orphan" });

        var act = async () => await db.SaveChangesAsync(ct);

        // A row stamped with nothing would fail the read filter forever — invisible to whoever created it.
        await act.Should().ThrowAsync<TenantScopeViolationException>();
    }

    [Fact]
    public async Task Insert_ForAnotherTenant_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        await using var scope = CreateScope(TenantA);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        db.Notes.Add(new Note { Name = "smuggled", TenantId = Guid.Parse(TenantB) });

        var act = async () => await db.SaveChangesAsync(ct);

        await act.Should().ThrowAsync<TenantScopeViolationException>();
    }

    [Fact]
    public async Task Update_OfAnotherTenantsRow_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var id = Guid.CreateVersion7();
        await using (var seed = CreateScope(TenantB)) {
            var seedDb = seed.ServiceProvider.GetRequiredService<TenantDbContext>();
            seedDb.Notes.Add(new Note { Id = id, Name = "b-owned" });
            await seedDb.SaveChangesAsync(ct);
        }

        await using var scope = CreateScope(TenantA);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        // Attaching by id sidesteps the read filter, so the write leg has to catch it on its own.
        var note = new Note { Id = id, Name = "hijacked", TenantId = Guid.Parse(TenantB) };
        db.Notes.Attach(note);
        db.Entry(note).Property(n => n.Name).IsModified = true;

        var act = async () => await db.SaveChangesAsync(ct);

        await act.Should().ThrowAsync<TenantScopeViolationException>();
    }

    [Fact]
    public async Task MovingARowBetweenTenants_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var name = $"movable-{Guid.CreateVersion7()}";
        await using (var seed = CreateScope(TenantA)) {
            var seedDb = seed.ServiceProvider.GetRequiredService<TenantDbContext>();
            seedDb.Notes.Add(new Note { Name = name });
            await seedDb.SaveChangesAsync(ct);
        }

        await using var scope = CreateScope(TenantA);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var note = await db.Notes.SingleAsync(n => n.Name == name, ct);
        note.TenantId = Guid.Parse(TenantB);

        var act = async () => await db.SaveChangesAsync(ct);

        await act.Should().ThrowAsync<TenantScopeViolationException>();
    }

    [Fact]
    public async Task SystemScope_CanWriteForAnyTenant() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var name = $"system-{Guid.CreateVersion7()}";
        await using (var scope = CreateScope(null)) {
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();

            using var _ = tenant.SystemScope();
            db.Notes.Add(new Note { Name = name, TenantId = Guid.Parse(TenantB) });
            await db.SaveChangesAsync(ct);
        }

        await using var reader = CreateScope(TenantB);
        (await reader.ServiceProvider.GetRequiredService<TenantDbContext>()
            .Notes.AnyAsync(note => note.Name == name, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task ExplicitScope_LetsABackgroundWorkerActPerTenant() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var name = $"per-tenant-{Guid.CreateVersion7()}";
        await using var scope = CreateScope(null);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        using (tenant.Scope(TenantA)) {
            db.Notes.Add(new Note { Name = name });
            await db.SaveChangesAsync(ct);
        }

        using (tenant.Scope(TenantA)) {
            (await db.Notes.AnyAsync(note => note.Name == name, ct)).Should().BeTrue();
        }

        using (tenant.Scope(TenantB)) {
            (await db.Notes.AnyAsync(note => note.Name == name, ct)).Should().BeFalse();
        }
    }

    private async Task<string> SeedAcrossTenantsAsync(CancellationToken ct) {
        var name = $"seeded-{Guid.CreateVersion7()}";
        foreach (var tenantId in new[] { TenantA, TenantB }) {
            await using var scope = CreateScope(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            db.Notes.Add(new Note { Name = name });
            await db.SaveChangesAsync(ct);
        }

        return name;
    }

    private AsyncServiceScope CreateScope(string? tenantId) {
        var services = new ServiceCollection();
        services.AddDbContext<TenantDbContext>(options => options.UseNpgsql(fixture.ConnectionString));

        // Registered before AddElarionTenantScoping, whose TryAdd then leaves the claim-based default out.
        services.AddSingleton<ITenantResolver>(new FixedTenantResolver(tenantId));
        services.AddElarionTenantScoping();
        services.AddElarionTenantScopingEntityFrameworkCore<TenantDbContext>();

        // The provider is owned by the returned scope so each test gets an isolated tenant context.
        var provider = services.BuildServiceProvider();
        return provider.CreateAsyncScope();
    }
}

/// <summary>A resolver standing in for whatever an application derives its tenant from.</summary>
internal sealed class FixedTenantResolver(string? tenantId) : ITenantResolver {
    public string? Resolve() {
        return tenantId;
    }
}

/// <summary>A tenant-scoped row; nothing about it mentions filtering or stamping.</summary>
public sealed class Note : ITenantScoped<Guid> {
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>Integration context mapping <see cref="Note"/> with the tenant filter applied.</summary>
public sealed class TenantDbContext(DbContextOptions<TenantDbContext> options) : DbContext(options) {
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<Note>(builder => {
            builder.ToTable("notes");
            builder.HasKey(note => note.Id);
            builder.Property(note => note.Id).ValueGeneratedNever();
            builder.Property(note => note.TenantId).HasColumnName("tenant_id");
            builder.Property(note => note.Name).HasColumnName("name");
        });

        modelBuilder.ApplyElarionTenantScoping(this);
    }
}

/// <summary>Starts a disposable PostgreSQL container and creates the notes schema once.</summary>
public sealed class TenantScopingFixture : IAsyncLifetime {
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

        // EnsureCreated runs against the model, and the model now carries a query filter that reads the
        // context's tenant — schema creation itself never queries, so no tenant is needed here.
        var services = new ServiceCollection();
        services.AddDbContext<TenantDbContext>(options => options.UseNpgsql(ConnectionString));
        services.AddSingleton<ITenantResolver>(new FixedTenantResolver(null));
        services.AddElarionTenantScoping();
        services.AddElarionTenantScopingEntityFrameworkCore<TenantDbContext>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TenantDbContext>().Database.EnsureCreatedAsync();

        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) await _container.DisposeAsync();
    }
}
