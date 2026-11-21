using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Authorization;
using Elarion.Authorization.EntityFrameworkCore;
using Elarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Authorization;

/// <summary>
/// The filter-backed point check against real PostgreSQL: <c>[RequireResource]</c> and the list query read one
/// composed <see cref="IQueryAuthorizer{TEntity}"/>, so they agree for every caller — owner, permission holder,
/// administrator, outsider — and a hidden record answers like a missing one. Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ResourceFilterCheckIntegrationTests(ResourceFilterCheckFixture fixture)
    : IClassFixture<ResourceFilterCheckFixture> {
    private static readonly Guid Alice = Guid.Parse("a11ce000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("b0b00000-0000-0000-0000-000000000002");

    // Unrestricted dossiers for everyone who may read them (reading only), any dossier for its owner and its
    // reviewer, everything for administrators.
    private static readonly IQueryAuthorizer<Dossier> Visibility = QueryAuthorizers.AnyOf(
        QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Dossier>()),
        QueryAuthorizers.ForOperations(
            QueryAuthorizers.WhenPermission(
                "dossiers.read", QueryAuthorizers.Rows<Dossier>(dossier => !dossier.IsRestricted)),
            ResourceOperation.Read),
        QueryAuthorizers.ForUser<Dossier, Guid>((dossier, userId) => dossier.OwnerId == userId),
        QueryAuthorizers.ForUser<Dossier, Guid>((dossier, userId) => dossier.ReviewerId == userId));

    private static FakeCurrentUser User(Guid id, string[]? roles = null, params string[] permissions) {
        return new FakeCurrentUser {
            IsAuthenticated = true,
            UserId = id.ToString(),
            Roles = roles ?? [],
            Claims = permissions.Select(permission => ("permission", permission)).ToArray()
        };
    }

    private ServiceProvider BuildProvider(
        ICurrentUser? currentUser = null,
        Action<IServiceCollection>? configure = null,
        Func<IQueryable<Dossier>, IQueryable<Dossier>>? rows = null) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DossierDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddScoped(_ => currentUser ?? User(Alice));
        services.AddElarionAuthorization();
        services.AddSingleton(Visibility);
        services.AddElarionResourceFilterCheck<DossierDbContext, Dossier, Guid>(dossier => dossier.Id, rows);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> SeedAsync(
        IServiceProvider scope, Guid owner, bool restricted = false, Guid? reviewer = null, bool archived = false) {
        var db = scope.GetRequiredService<DossierDbContext>();
        var dossier = new Dossier {
            Id = Guid.CreateVersion7(), OwnerId = owner, IsRestricted = restricted, ReviewerId = reviewer,
            IsArchived = archived
        };
        db.Dossiers.Add(dossier);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return dossier.Id;
    }

    private static ValueTask<bool> CheckAsync(
        IServiceProvider scope, ICurrentUser user, object? id, ResourceOperation? operation = null) {
        return scope.GetRequiredService<IResourceAuthorizer>().AuthorizeResourceAsync(
            new ResourceAuthorizationContext(
                user, typeof(Dossier), ResourceTypeDiscriminator.For(typeof(Dossier)),
                operation ?? ResourceOperation.Read, id),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PointCheck_AgreesWithTheListFilter_ForEveryCaller() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DossierDbContext>();
        Guid[] seeded = [
            await SeedAsync(scope.ServiceProvider, Alice),
            await SeedAsync(scope.ServiceProvider, Alice, restricted: true),
            await SeedAsync(scope.ServiceProvider, Bob, restricted: true, reviewer: Alice),
            await SeedAsync(scope.ServiceProvider, Bob, restricted: true),
            await SeedAsync(scope.ServiceProvider, Bob)
        ];
        ICurrentUser[] callers = [
            User(Alice),
            User(Alice, permissions: "dossiers.read"),
            User(Bob, permissions: "dossiers.write"),
            User(Guid.CreateVersion7(), ["Admin"]),
            User(Guid.CreateVersion7()),
            new FakeCurrentUser { IsAuthenticated = true, UserId = "not-a-guid", Claims = [("permission", "dossiers.read")] },
            new FakeCurrentUser { IsAuthenticated = false, UserId = Alice.ToString() }
        ];

        foreach (var caller in callers) {
            var listed = await db.Dossiers
                .Where(dossier => seeded.Contains(dossier.Id))
                .WhereAuthorized(Visibility, caller)
                .Select(dossier => dossier.Id)
                .ToListAsync(ct);

            foreach (var id in seeded)
                (await CheckAsync(scope.ServiceProvider, caller, id))
                    .Should().Be(listed.Contains(id), "the point check and the list read one rule");
        }

        // The rule is not vacuous: the callers see different sets.
        (await CheckAsync(scope.ServiceProvider, callers[0], seeded[2])).Should().BeTrue("Alice reviews it");
        (await CheckAsync(scope.ServiceProvider, callers[0], seeded[3])).Should().BeFalse();
        (await CheckAsync(scope.ServiceProvider, callers[1], seeded[4])).Should().BeTrue("dossiers.read sees unrestricted");
        (await CheckAsync(scope.ServiceProvider, callers[3], seeded[3])).Should().BeTrue("administrators see everything");
        (await CheckAsync(scope.ServiceProvider, callers[4], seeded[4])).Should().BeFalse();
    }

    [Fact]
    public async Task PointCheck_PassesTheOperationToTheFilter() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var bobs = await SeedAsync(scope.ServiceProvider, Bob);
        var reader = User(Alice, permissions: "dossiers.read");

        (await CheckAsync(scope.ServiceProvider, reader, bobs, ResourceOperation.Read)).Should().BeTrue();
        (await CheckAsync(scope.ServiceProvider, reader, bobs, ResourceOperation.Update))
            .Should().BeFalse("reading an unrestricted dossier does not grant changing it");
        (await CheckAsync(scope.ServiceProvider, User(Bob), bobs, ResourceOperation.Update)).Should().BeTrue();
    }

    [Fact]
    public async Task PointCheck_DeniesAnIdThatNamesNoRow_EvenForAnUnrestrictedCaller() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var admin = User(Guid.CreateVersion7(), ["Admin"]);
        var existing = await SeedAsync(scope.ServiceProvider, Bob, restricted: true);

        (await CheckAsync(scope.ServiceProvider, admin, existing)).Should().BeTrue();
        (await CheckAsync(scope.ServiceProvider, admin, Guid.CreateVersion7())).Should().BeFalse("no such row");
        (await CheckAsync(scope.ServiceProvider, admin, null)).Should().BeFalse("the request named no id");
        (await CheckAsync(scope.ServiceProvider, admin, "not-a-key")).Should().BeFalse();
        (await CheckAsync(scope.ServiceProvider, admin, 42)).Should().BeFalse("an int is not this entity's key");
    }

    [Fact]
    public async Task PointCheck_AcceptsTheKeyAsInvariantText() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var mine = await SeedAsync(scope.ServiceProvider, Alice, restricted: true);

        (await CheckAsync(scope.ServiceProvider, User(Alice), mine.ToString())).Should().BeTrue();
        (await CheckAsync(scope.ServiceProvider, User(Bob), mine.ToString())).Should().BeFalse();
    }

    [Fact]
    public async Task PointCheck_RespectsTheSetsQueryFilters_UnlessTheCheckIsToldOtherwise() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        Guid archived;
        await using (var provider = BuildProvider()) {
            await using var scope = provider.CreateAsyncScope();
            archived = await SeedAsync(scope.ServiceProvider, Alice, archived: true);

            (await CheckAsync(scope.ServiceProvider, User(Alice), archived))
                .Should().BeFalse("an archived dossier is not in the context's set");
        }

        await using var seeingArchived = BuildProvider(rows: rows => rows.IgnoreQueryFilters());
        await using var seeingScope = seeingArchived.CreateAsyncScope();
        (await CheckAsync(seeingScope.ServiceProvider, User(Alice), archived)).Should().BeTrue();
        (await CheckAsync(seeingScope.ServiceProvider, User(Bob), archived))
            .Should().BeFalse("seeing past the archive flag does not lift the rule");
    }

    [Fact]
    public async Task RequireResource_AnswersAHiddenAndAMissingRecordAlike() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider(User(Alice));
        await using var scope = provider.CreateAsyncScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IAuthorizer>();
        var mine = await SeedAsync(scope.ServiceProvider, Alice, restricted: true);
        var hidden = await SeedAsync(scope.ServiceProvider, Bob, restricted: true);

        AuthorizationRequirements Requirement(Guid id, ResourceDenial denyAs) {
            return new AuthorizationRequirements(false, false, [], [], [], [], [
                new ResourceRequirement(
                    typeof(Dossier), ResourceTypeDiscriminator.For(typeof(Dossier)), ResourceOperation.Read, id) {
                    DenyAs = denyAs
                }
            ]);
        }

        (await authorizer.AuthorizeAsync(Requirement(mine, ResourceDenial.NotFound), null, ct)).Should().BeNull();
        var hiddenError = await authorizer.AuthorizeAsync(Requirement(hidden, ResourceDenial.NotFound), null, ct);
        var missingError =
            await authorizer.AuthorizeAsync(Requirement(Guid.CreateVersion7(), ResourceDenial.NotFound), null, ct);

        hiddenError!.Kind.Should().Be(ErrorKind.NotFound);
        missingError.Should().BeEquivalentTo(hiddenError, "a hidden record is indistinguishable from a missing one");
        (await authorizer.AuthorizeAsync(Requirement(hidden, ResourceDenial.Forbidden), null, ct))!
            .Kind.Should().Be(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task GrantsBackend_StillRoutesAFilterCheckedTypeToItsFilter() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        // The grants backend replaces the default IResourceAuthorizer; a type with a filter check keeps its check.
        await using var provider = BuildProvider(
            configure: services => services.AddElarionResourceAuthorization<DossierDbContext>());
        await using var scope = provider.CreateAsyncScope();
        var mine = await SeedAsync(scope.ServiceProvider, Alice, restricted: true);
        var granted = Guid.CreateVersion7();
        await scope.ServiceProvider.GetRequiredService<IResourceGrantStore>().GrantAsync(
            new ResourceGrant("Report", granted.ToString(), ResourcePrincipal.User(Alice.ToString()),
                ResourceOperation.Read), ct);

        (await CheckAsync(scope.ServiceProvider, User(Alice), mine))
            .Should().BeTrue("the owner rule is part of the filter, which no grant records");
        (await scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>().AuthorizeResourceAsync(
                new ResourceAuthorizationContext(User(Alice), typeof(Report), "Report", ResourceOperation.Read, granted),
                ct))
            .Should().BeTrue("a type without a filter check is still answered by its grants");
    }

    [Fact]
    public async Task ForUser_ReachesTheDatabaseAsAParameter_AndTheComplementCoversNullColumns() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DossierDbContext>();
        Guid[] seeded = [
            await SeedAsync(scope.ServiceProvider, Bob, restricted: true, reviewer: Alice),
            await SeedAsync(scope.ServiceProvider, Bob, restricted: true),
            await SeedAsync(scope.ServiceProvider, Bob, restricted: true, reviewer: Bob)
        ];
        var reviewed = QueryAuthorizers.ForUser<Dossier, Guid>((dossier, userId) => dossier.ReviewerId == userId);
        var mine = db.Dossiers.Where(dossier => seeded.Contains(dossier.Id));

        var visibleQuery = mine.WhereAuthorized(reviewed, User(Alice));
        var hiddenQuery = mine.WhereNotAuthorized(reviewed, User(Alice));

        // ToQueryString lists parameter values in leading comment lines; the statement itself must not inline the id.
        var statement = string.Join('\n', visibleQuery.ToQueryString().Split('\n').Where(line => !line.StartsWith("--")));
        statement.Should().NotContain(Alice.ToString(), "the caller's id is a parameter").And.Contain("@");
        (await visibleQuery.Select(dossier => dossier.Id).ToListAsync(ct)).Should().Equal(seeded[0]);
        // reviewer_id IS NULL makes the SQL comparison NULL; the complement must still contain that row.
        (await hiddenQuery.Select(dossier => dossier.Id).ToListAsync(ct))
            .Should().BeEquivalentTo([seeded[1], seeded[2]]);
    }

    [Fact]
    public void AddElarionResourceFilterCheck_RefusesAKeyAQueryCannotCompare() {
        var register = () => new ServiceCollection()
            .AddElarionResourceFilterCheck<DossierDbContext, Dossier, Uncomparable>(_ => new Uncomparable());

        register.Should().Throw<ArgumentException>().WithMessage("*cannot be compared*");
    }

    private sealed class Report;

    private readonly struct Uncomparable;
}

/// <summary>A record with an owner, an optional reviewer, a restriction flag and an archive flag.</summary>
public sealed class Dossier {
    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    public Guid? ReviewerId { get; set; }

    public bool IsRestricted { get; set; }

    public bool IsArchived { get; set; }
}

/// <summary>Maps <see cref="Dossier"/> with an archive query filter, plus the resource-grants table.</summary>
public sealed class DossierDbContext(DbContextOptions<DossierDbContext> options) : DbContext(options) {
    public DbSet<Dossier> Dossiers => Set<Dossier>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<Dossier>(builder => {
            builder.ToTable("dossiers");
            builder.HasKey(dossier => dossier.Id);
            builder.HasQueryFilter(dossier => !dossier.IsArchived);
        });
        modelBuilder.ApplyElarionResourceGrants();
    }
}

/// <summary>Starts a disposable PostgreSQL container and creates the dossier schema once.</summary>
public sealed class ResourceFilterCheckFixture : IAsyncLifetime {
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
        await using var context = new DossierDbContext(
            new DbContextOptionsBuilder<DossierDbContext>().UseNpgsql(ConnectionString).Options);
        await context.Database.EnsureCreatedAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        await TestContainerLifecycle.Shared.DisposeAsync(_container);
    }
}
