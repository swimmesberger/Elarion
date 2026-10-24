using AwesomeAssertions;
using Elarion.Abstractions.Messaging;
using Elarion.EntityFrameworkCore.Lifecycle;
using Elarion.EntityFrameworkCore.MultiTenancy;
using Elarion.Messaging;
using Elarion.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.EntityFrameworkCore;

/// <summary>
/// Entity lifecycle events against real PostgreSQL: a deletion of a <see cref="PublishLifecycleEventsAttribute"/> entity
/// publishes <see cref="EntitiesDeleted{TEntity}"/> inline before the DELETE — including rows a database cascade takes
/// with an untracked principal — consumers clean up soft-referencing data in the same save (to a fixed point), and the
/// paths that would bypass the event are refused.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EntityLifecycleEventsIntegrationTests(LifecycleFixture fixture) : IClassFixture<LifecycleFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Guid Ledger, Guid Account, Guid Remark, Guid File)> SeedAsync() {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();
        var ledger = new Ledger { Id = Guid.CreateVersion7() };
        var account = new Account { Id = Guid.CreateVersion7(), LedgerId = ledger.Id };
        var remark = new Remark { Id = Guid.CreateVersion7(), OwnerId = account.Id };
        var file = new RemarkFile { Id = Guid.CreateVersion7(), OwnerId = remark.Id };
        db.AddRange(ledger, account, remark, file);
        await db.SaveChangesAsync(Ct);
        return (ledger.Id, account.Id, remark.Id, file.Id);
    }

    private async Task<bool> ExistsAsync<T>(Guid id) where T : class, IHasId {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();
        return await db.Set<T>().AnyAsync(row => row.Id == id, Ct);
    }

    [Fact]
    public async Task TrackedDelete_PublishesAndConsumersCleanUpSoftReferencesInTheSameSave() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var seeded = await SeedAsync();

        await using (var scope = fixture.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();
            db.Accounts.Remove(await db.Accounts.SingleAsync(account => account.Id == seeded.Account, Ct));
            await db.SaveChangesAsync(Ct);
        }

        fixture.Published.Should().ContainEquivalentOf(("Account", seeded.Account));
        // The remark consumer's deletion is itself published (Remark is marked), so its files go too.
        fixture.Published.Should().ContainEquivalentOf(("Remark", seeded.Remark));
        (await ExistsAsync<Account>(seeded.Account)).Should().BeFalse();
        (await ExistsAsync<Remark>(seeded.Remark)).Should().BeFalse();
        (await ExistsAsync<RemarkFile>(seeded.File)).Should().BeFalse();
        (await ExistsAsync<Ledger>(seeded.Ledger)).Should().BeTrue();
    }

    [Fact]
    public async Task DatabaseCascade_FromAnUnwatchedPrincipal_PublishesTheUntrackedRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var seeded = await SeedAsync();

        await using (var scope = fixture.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();
            // Only the ledger is tracked; PostgreSQL cascades to its account, which the change tracker never sees.
            db.Ledgers.Remove(await db.Ledgers.SingleAsync(ledger => ledger.Id == seeded.Ledger, Ct));
            await db.SaveChangesAsync(Ct);
        }

        fixture.Published.Should().ContainEquivalentOf(("Account", seeded.Account));
        (await ExistsAsync<Account>(seeded.Account)).Should().BeFalse();
        (await ExistsAsync<Remark>(seeded.Remark)).Should().BeFalse();
        (await ExistsAsync<RemarkFile>(seeded.File)).Should().BeFalse();
    }

    [Fact]
    public async Task ManyScopes_ShareOneInterceptorAndOneEfServiceProvider() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        // A per-scope interceptor instance would key a new EF internal service provider per scope, and EF fails past
        // twenty (ManyServiceProvidersCreatedWarning) — every request of a real host would build one.
        for (var i = 0; i < 30; i++) {
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();
            await db.Ledgers.Where(ledger => ledger.Id == Guid.Empty).ToListAsync(Ct);
        }
    }

    [Fact]
    public async Task ConsumerFailure_FailsTheSave_AndNothingIsDeleted() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var seeded = await SeedAsync();
        fixture.FailFor = seeded.Account;

        await using (var scope = fixture.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();
            db.Accounts.Remove(await db.Accounts.SingleAsync(account => account.Id == seeded.Account, Ct));

            var save = async () => await db.SaveChangesAsync(Ct);

            await save.Should().ThrowAsync<Exception>();
        }

        (await ExistsAsync<Account>(seeded.Account)).Should().BeTrue();
        (await ExistsAsync<Remark>(seeded.Remark)).Should().BeTrue();
    }

    [Fact]
    public async Task SynchronousSave_OfAWatchedDeletion_IsRefused() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var seeded = await SeedAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();
        db.Ledgers.Remove(await db.Ledgers.SingleAsync(ledger => ledger.Id == seeded.Ledger, Ct));

        var save = () => db.SaveChanges();

        save.Should().Throw<InvalidOperationException>().WithMessage("*SaveChangesAsync*");
    }

    [Fact]
    public async Task ExecuteDelete_ThatWouldBypassTheEvent_IsRefused() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var seeded = await SeedAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();

        var watched = async () => await db.Accounts.Where(account => account.Id == seeded.Account).ExecuteDeleteAsync(Ct);
        var cascading = async () => await db.Ledgers.Where(ledger => ledger.Id == seeded.Ledger).ExecuteDeleteAsync(Ct);
        var synchronous = () => db.Accounts.Where(account => account.Id == seeded.Account).ExecuteDelete();
        // The query's root is an unwatched set; the rows deleted are the joined accounts.
        var joined = async () => await db.RemarkFiles
            .Join(db.Accounts, file => file.OwnerId, account => account.Id, (_, account) => account)
            .ExecuteDeleteAsync(Ct);
        await watched.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ExecuteDelete on Account*");
        await cascading.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ExecuteDelete on Ledger*");
        synchronous.Should().Throw<InvalidOperationException>().WithMessage("*ExecuteDelete on Account*");
        await joined.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ExecuteDelete on Account*");

        // A set that reaches no watched entity is unaffected.
        (await db.RemarkFiles.Where(file => file.Id == seeded.File).ExecuteDeleteAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task ConsumerThatSaves_InsideATransaction_PublishesOnce_AndCleansUp() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var seeded = await SeedAsync();
        fixture.SaveInConsumer = seeded.Account;

        await using (var scope = fixture.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Accounts.Remove(await db.Accounts.SingleAsync(account => account.Id == seeded.Account, Ct));
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        // The consumer's own save re-entered the interceptor; the shared record keeps it from publishing again.
        fixture.Published.Count(published => published == ("Account", seeded.Account)).Should().Be(1);
        fixture.Published.Should().ContainEquivalentOf(("Remark", seeded.Remark));
        (await ExistsAsync<Account>(seeded.Account)).Should().BeFalse();
        (await ExistsAsync<RemarkFile>(seeded.File)).Should().BeFalse();
    }

    [Fact]
    public async Task ConsumerThatSaves_OutsideATransaction_IsRefused_AndNothingIsDeleted() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var seeded = await SeedAsync();
        fixture.SaveInConsumer = seeded.Account;

        await using (var scope = fixture.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();
            db.Accounts.Remove(await db.Accounts.SingleAsync(account => account.Id == seeded.Account, Ct));

            var save = async () => await db.SaveChangesAsync(Ct);

            await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*outside a transaction*");
        }

        (await ExistsAsync<Account>(seeded.Account)).Should().BeTrue();
        (await ExistsAsync<Remark>(seeded.Remark)).Should().BeTrue();
    }

    [Fact]
    public async Task ReparentedDependent_OfADeletedPrincipal_IsNotReported() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var seeded = await SeedAsync();
        var target = new Ledger { Id = Guid.CreateVersion7() };

        await using (var scope = fixture.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();
            db.Ledgers.Add(target);
            // Merge: the account moves to the new ledger in the same save that deletes its old one.
            (await db.Accounts.SingleAsync(account => account.Id == seeded.Account, Ct)).LedgerId = target.Id;
            db.Ledgers.Remove(await db.Ledgers.SingleAsync(ledger => ledger.Id == seeded.Ledger, Ct));
            await db.SaveChangesAsync(Ct);
        }

        fixture.Published.Should().NotContainEquivalentOf(("Account", seeded.Account));
        (await ExistsAsync<Account>(seeded.Account)).Should().BeTrue();
        (await ExistsAsync<Remark>(seeded.Remark)).Should().BeTrue();
    }

    [Fact]
    public async Task OrphanDeletedOnlyAtSaveTime_IsPublished() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var seeded = await SeedAsync();

        await using (var scope = fixture.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();
            db.ChangeTracker.DeleteOrphansTiming = CascadeTiming.OnSaveChanges;
            var ledger = await db.Ledgers.Include(row => row.Accounts).SingleAsync(row => row.Id == seeded.Ledger, Ct);
            // A required relationship severed: EF Core deletes the account only when the save runs.
            ledger.Accounts.Clear();
            await db.SaveChangesAsync(Ct);
        }

        fixture.Published.Should().ContainEquivalentOf(("Account", seeded.Account));
        (await ExistsAsync<Account>(seeded.Account)).Should().BeFalse();
        (await ExistsAsync<Remark>(seeded.Remark)).Should().BeFalse();
    }

    [Fact]
    public async Task ContextFromAFactory_IsRefusedWhenItSavesADeletion() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var seeded = await SeedAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<LifecycleDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddElarionEntityLifecycleEvents<LifecycleDbContext>();
        services.AddElarionDomainEventBus();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var db = await provider.GetRequiredService<IDbContextFactory<LifecycleDbContext>>()
            .CreateDbContextAsync(Ct);
        db.Accounts.Remove(await db.Accounts.SingleAsync(account => account.Id == seeded.Account, Ct));

        var save = async () => await db.SaveChangesAsync(Ct);

        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*resolved from a scope*");
        (await ExistsAsync<Account>(seeded.Account)).Should().BeTrue();
    }
}

public sealed class LifecycleFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = "";

    public ServiceProvider Services { get; private set; } = null!;

    /// <summary>Every (entity, id) the consumers saw, across the tests (ids are unique per test).</summary>
    public System.Collections.Concurrent.ConcurrentBag<(string Entity, Guid Id)> Published { get; } = [];

    /// <summary>An account id whose deletion the account consumer fails on.</summary>
    public Guid? FailFor { get; set; }

    /// <summary>An account id whose deletion the account consumer follows with its own save, as a reused command would.</summary>
    public Guid? SaveInConsumer { get; set; }

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
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<LifecycleDbContext>(options => options.UseNpgsql(ConnectionString));
        services.AddElarionEntityLifecycleEvents<LifecycleDbContext>();
        services.AddElarionDomainEventBus();
        services.AddSingleton(Consumer<EntitiesDeleted<Account>>(async (sp, deleted, ct) => {
            foreach (var id in deleted.Ids) Published.Add(("Account", id));
            if (FailFor is { } fail && deleted.Ids.Contains(fail)) throw new InvalidOperationException("consumer failed");

            var db = sp.GetRequiredService<LifecycleDbContext>();
            db.Remarks.RemoveRange(await db.Remarks.Where(remark => deleted.Ids.Contains(remark.OwnerId)).ToListAsync(ct));
            if (SaveInConsumer is { } save && deleted.Ids.Contains(save)) await db.SaveChangesAsync(ct);
        }));
        services.AddSingleton(Consumer<EntitiesDeleted<Remark>>(async (sp, deleted, ct) => {
            foreach (var id in deleted.Ids) Published.Add(("Remark", id));
            var db = sp.GetRequiredService<LifecycleDbContext>();
            db.RemarkFiles.RemoveRange(await db.RemarkFiles.Where(file => deleted.Ids.Contains(file.OwnerId)).ToListAsync(ct));
        }));
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using (var scope = Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<LifecycleDbContext>().Database.EnsureCreatedAsync();

        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (Services is not null) await Services.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    private static EventSubscriptionDescriptor Consumer<TEvent>(
        Func<IServiceProvider, TEvent, CancellationToken, Task> handle) where TEvent : IDomainEvent {
        return new EventSubscriptionDescriptor {
            EventType = typeof(TEvent),
            Plane = EventPlane.Domain,
            ServiceType = typeof(LifecycleFixture),
            InvokeAsync = async (sp, evt, _, ct) => await handle(sp, (TEvent)evt, ct)
        };
    }
}

public interface IHasId {
    Guid Id { get; }
}

/// <summary>Not watched; deleting one cascades (in the database) to its accounts.</summary>
public sealed class Ledger : IHasId {
    public required Guid Id { get; init; }

    public List<Account> Accounts { get; } = [];
}

[PublishLifecycleEvents]
public sealed class Account : IHasId {
    public required Guid Id { get; init; }

    public required Guid LedgerId { get; set; }
}

/// <summary>Points at an account by id, without a foreign key; itself a soft-reference target for files.</summary>
[PublishLifecycleEvents]
public sealed class Remark : IHasId {
    public required Guid Id { get; init; }

    public required Guid OwnerId { get; init; }
}

public sealed class RemarkFile : IHasId {
    public required Guid Id { get; init; }

    public required Guid OwnerId { get; init; }
}

public sealed class LifecycleDbContext(DbContextOptions<LifecycleDbContext> options) : DbContext(options) {
    public DbSet<Ledger> Ledgers => Set<Ledger>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Remark> Remarks => Set<Remark>();

    public DbSet<RemarkFile> RemarkFiles => Set<RemarkFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<Ledger>().Property(ledger => ledger.Id).ValueGeneratedNever();
        modelBuilder.Entity<Account>(account => {
            account.Property(row => row.Id).ValueGeneratedNever();
            account.HasOne<Ledger>().WithMany(ledger => ledger.Accounts).HasForeignKey(row => row.LedgerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Remark>().Property(remark => remark.Id).ValueGeneratedNever();
        modelBuilder.Entity<RemarkFile>().Property(file => file.Id).ValueGeneratedNever();
    }
}

public sealed class EntityLifecyclePlanTests {
    [PublishLifecycleEvents]
    public sealed class Counter {
        public required int Id { get; init; }
    }

    private sealed class IntKeyedContext(DbContextOptions<IntKeyedContext> options) : DbContext(options) {
        public DbSet<Counter> Counters => Set<Counter>();
    }

    public class Animal {
        public required Guid Id { get; init; }
    }

    [PublishLifecycleEvents]
    public sealed class Dog : Animal;

    private sealed class HierarchyContext(DbContextOptions<HierarchyContext> options) : DbContext(options) {
        public DbSet<Animal> Animals => Set<Animal>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) {
            modelBuilder.Entity<Dog>();
        }
    }

    public sealed class Region {
        public required Guid Id { get; init; }

        public required string Code { get; init; }
    }

    [PublishLifecycleEvents]
    public sealed class Office {
        public required Guid Id { get; init; }

        public required string RegionCode { get; init; }
    }

    private sealed class AlternateKeyContext(DbContextOptions<AlternateKeyContext> options) : DbContext(options) {
        public DbSet<Office> Offices => Set<Office>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) {
            modelBuilder.Entity<Office>().HasOne<Region>().WithMany().HasForeignKey(office => office.RegionCode)
                .HasPrincipalKey(region => region.Code).OnDelete(DeleteBehavior.Cascade);
        }
    }

    [Fact]
    public async Task MarkedEntityInAHierarchy_IsRejected() {
        await using var provider = Provider<HierarchyContext>();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HierarchyContext>();
        var dog = new Dog { Id = Guid.CreateVersion7() };
        db.Animals.Attach(dog);
        db.Animals.Remove(dog);

        var save = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*inheritance hierarchy*");
    }

    [Fact]
    public async Task CascadeThroughAnAlternateKey_IsRejected() {
        await using var provider = Provider<AlternateKeyContext>();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AlternateKeyContext>();
        var office = new Office { Id = Guid.CreateVersion7(), RegionCode = "eu" };
        db.Offices.Attach(office);
        db.Offices.Remove(office);

        var save = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*alternate key*");
    }

    [Fact]
    public async Task RegisteredAfterTenantScoping_FailsWhenTheContextIsBuilt() {
        var services = new ServiceCollection();
        services.AddDbContext<IntKeyedContext>(options => options.UseNpgsql("Host=unused.invalid"));
        services.AddScoped<Elarion.Abstractions.Identity.ICurrentUser>(_ =>
            new Elarion.Identity.ClaimsPrincipalCurrentUser(new Elarion.Identity.ClaimsCurrentUserOptions()));
        services.AddElarionTenantScoping();
        services.AddElarionTenantScopingEntityFrameworkCore<IntKeyedContext>();
        services.AddElarionEntityLifecycleEvents<IntKeyedContext>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var build = () => scope.ServiceProvider.GetRequiredService<IntKeyedContext>().Model;

        build.Should().Throw<InvalidOperationException>().WithMessage("*before AddElarionTenantScopingEntityFrameworkCore*");
    }

    // The model is built and checked before any statement runs, so no database is needed.
    private static ServiceProvider Provider<TContext>() where TContext : DbContext {
        var services = new ServiceCollection();
        services.AddDbContext<TContext>(options => options.UseNpgsql("Host=unused.invalid"));
        services.AddElarionEntityLifecycleEvents<TContext>();
        services.AddElarionDomainEventBus();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task MarkedEntityWithoutASingleGuidKey_IsRejectedOnTheFirstSave() {
        var services = new ServiceCollection();
        // The model is built and checked before any statement runs, so no database is needed.
        services.AddDbContext<IntKeyedContext>(options => options.UseNpgsql("Host=unused.invalid"));
        services.AddElarionEntityLifecycleEvents<IntKeyedContext>();
        services.AddElarionDomainEventBus();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IntKeyedContext>();
        var counter = new Counter { Id = 1 };
        db.Counters.Attach(counter);
        db.Counters.Remove(counter);

        var save = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no single Guid primary key*");
    }
}
