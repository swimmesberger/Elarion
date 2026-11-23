using System.Linq.Expressions;
using AwesomeAssertions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Authorization.EntityFrameworkCore;
using Elarion.EntityFrameworkCore.Identity;
using Elarion.Tests.Identity;
using Elarion.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Authorization;

/// <summary>
/// "Who may access this record" against real PostgreSQL: <c>WhereAuthorizedFor</c> over the Identity-backed principal
/// directory answers in one statement and agrees with the forward filter for principals built from the same rows.
/// Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PrincipalDirectoryIntegrationTests(PrincipalDirectoryFixture fixture)
    : IClassFixture<PrincipalDirectoryFixture> {
    private const string Permission = "permission";

    private ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null) {
        var services = new ServiceCollection();
        services.AddDbContext<ReadersDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddElarionResourceAuthorization<ReadersDbContext>();
        configure?.Invoke(services);
        services.AddElarionIdentityPrincipalDirectory<ReadersDbContext, ApplicationRole, Guid>();
        return services.BuildServiceProvider();
    }

    // Seeds accounts by name; each test uses its own ids and role names, so the tests share one database.
    private sealed class Cast(ReadersDbContext db) {
        private readonly Dictionary<string, Guid> _users = [];
        private readonly Dictionary<string, ApplicationRole> _roles = [];
        private readonly string _suffix = Guid.CreateVersion7().ToString("N");

        public Guid this[string name] => _users[name];

        public string Role(string name) {
            return $"{name}-{_suffix}";
        }

        public IQueryable<Guid> Everyone => db.Set<ApplicationUser>()
            .Where(user => _users.Values.Contains(user.Id))
            .Select(user => user.Id);

        public Cast Add(
            string name, string[]? roles = null, string[]? permissions = null, bool phoneConfirmed = false) {
            var user = new ApplicationUser {
                Id = Guid.CreateVersion7(), UserName = $"{name}-{_suffix}", PhoneNumberConfirmed = phoneConfirmed
            };
            _users[name] = user.Id;
            db.Add(user);
            foreach (var role in roles ?? [])
                db.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = EnsureRole(role).Id });
            foreach (var permission in permissions ?? [])
                db.Add(new IdentityUserClaim<Guid> { UserId = user.Id, ClaimType = Permission, ClaimValue = permission });
            return this;
        }

        public Cast RoleHolds(string role, string permission) {
            db.Add(new IdentityRoleClaim<Guid> {
                RoleId = EnsureRole(role).Id, ClaimType = Permission, ClaimValue = permission
            });
            return this;
        }

        private ApplicationRole EnsureRole(string name) {
            if (_roles.TryGetValue(name, out var existing)) return existing;

            var role = new ApplicationRole {
                Id = Guid.CreateVersion7(), Name = Role(name), NormalizedName = Role(name).ToUpperInvariant()
            };
            _roles[name] = role;
            db.Add(role);
            return role;
        }

        public IEnumerable<string> Named(IEnumerable<Guid> ids) {
            return ids.Select(id => _users.Single(user => user.Value == id).Key).Order();
        }
    }

    private static async Task<Dossier> SeedDossierAsync(ReadersDbContext db, Guid owner, bool restricted) {
        var dossier = new Dossier { Id = Guid.CreateVersion7(), OwnerId = owner, IsRestricted = restricted };
        db.Add(dossier);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return dossier;
    }

    [Fact]
    public async Task Readers_OfAComposedRule_AreReadFromUserRolesRoleClaimsAndUserClaims_InOneStatement() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReadersDbContext>();
        var directory = scope.ServiceProvider.GetRequiredService<IPrincipalDirectory<Guid>>();
        var cast = new Cast(db)
            .Add("owner")
            .Add("auditor", roles: ["Auditor"])
            .Add("clerk", permissions: ["dossiers.read"])
            .Add("admin", roles: ["Admin"])
            .Add("writer", permissions: ["dossiers.write"])
            .Add("nobody", roles: ["Staff"])
            .RoleHolds("Auditor", "dossiers.read");
        var open = await SeedDossierAsync(db, cast["owner"], restricted: false);
        var restricted = await SeedDossierAsync(db, cast["owner"], restricted: true);
        var rule = QueryAuthorizers.AnyOf(
            QueryAuthorizers.WhenRole(cast.Role("Admin"), QueryAuthorizers.Unrestricted<Dossier>()),
            QueryAuthorizers.WhenPermission(
                "dossiers.read", QueryAuthorizers.Rows<Dossier>(dossier => !dossier.IsRestricted)),
            QueryAuthorizers.ForUser<Dossier, Guid>((dossier, userId) => dossier.OwnerId == userId));

        var openReaders = cast.Everyone.WhereAuthorizedFor(
            db.Dossiers.Where(dossier => dossier.Id == open.Id), rule, directory);
        var restrictedReaders = cast.Everyone.WhereAuthorizedFor(
            db.Dossiers.Where(dossier => dossier.Id == restricted.Id), rule, directory);

        // The rule, the directory and the candidates join into a single SELECT.
        openReaders.ToQueryString().Split("SELECT").Length.Should().BeGreaterThan(2, "the gates are subqueries");
        cast.Named(await openReaders.ToListAsync(ct)).Should().Equal("admin", "auditor", "clerk", "owner");
        cast.Named(await restrictedReaders.ToListAsync(ct)).Should().Equal("admin", "owner");

        // The same accounts, as the principals Identity's claims factory would build, pass the forward filter.
        (string Name, FakeCurrentUser User)[] principals = [
            ("owner", Principal(cast["owner"])),
            ("auditor", Principal(cast["auditor"], [cast.Role("Auditor")], "dossiers.read")),
            ("clerk", Principal(cast["clerk"], permissions: "dossiers.read")),
            ("admin", Principal(cast["admin"], [cast.Role("Admin")])),
            ("writer", Principal(cast["writer"], permissions: "dossiers.write")),
            ("nobody", Principal(cast["nobody"], [cast.Role("Staff")]))
        ];
        foreach (var dossier in new[] { open, restricted }) {
            var forward = new List<string>();
            foreach (var (name, user) in principals)
                if (await db.Dossiers.WhereAuthorized(rule, user).AnyAsync(row => row.Id == dossier.Id, ct))
                    forward.Add(name);

            var inverse = await cast.Everyone
                .WhereAuthorizedFor(db.Dossiers.Where(row => row.Id == dossier.Id), rule, directory)
                .ToListAsync(ct);
            cast.Named(inverse).Should().Equal(forward.Order(), "both directions read one rule");
        }
    }

    [Fact]
    public async Task Readers_OfAGeneratedShapeSharedFilter_IncludeUserAndRoleGrants_ForTheOperationAsked() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReadersDbContext>();
        var directory = scope.ServiceProvider.GetRequiredService<IPrincipalDirectory<Guid>>();
        var grants = scope.ServiceProvider.GetRequiredService<IResourceGrantStore>();
        var cast = new Cast(db)
            .Add("owner")
            .Add("friend")
            .Add("reviewer", roles: ["Reviewers"])
            .Add("manager", roles: ["Managers"])
            .Add("stranger", roles: ["Staff"])
            .RoleHolds("Managers", "dossiers.manage");
        var dossier = await SeedDossierAsync(db, cast["owner"], restricted: true);
        await grants.GrantAsync(new ResourceGrant(
            "Dossier", dossier.Id.ToString(), ResourcePrincipal.User(cast["friend"].ToString()), ResourceOperation.Read), ct);
        await grants.GrantAsync(new ResourceGrant(
            "Dossier", dossier.Id.ToString(), ResourcePrincipal.Role(cast.Role("Reviewers")), ResourceOperation.Read), ct);
        var access = new DossierAccess(scope.ServiceProvider.GetRequiredService<IResourceGrantSource>());
        var row = db.Dossiers.Where(candidate => candidate.Id == dossier.Id);

        var readers = await cast.Everyone.WhereAuthorizedFor(row, access, directory).ToListAsync(ct);
        var editors = await cast.Everyone
            .WhereAuthorizedFor(row, access, directory, ResourceOperation.Update).ToListAsync(ct);

        cast.Named(readers).Should().Equal("friend", "manager", "owner", "reviewer");
        cast.Named(editors).Should().Equal(["manager", "owner"], "the shares are for reading");
    }

    [Fact]
    public async Task ApplicationDirectory_AddsWhatTheHostAddsToThePrincipal_AndIsKeptByTheRegistration() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        // A host that makes some accounts administrators while it builds the principal mirrors that in its directory.
        await using var provider = BuildProvider(services =>
            services.AddScoped<IPrincipalDirectory<Guid>, FlaggedAdministratorsDirectory>());
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReadersDbContext>();
        var directory = scope.ServiceProvider.GetRequiredService<IPrincipalDirectory<Guid>>();
        var cast = new Cast(db)
            .Add("owner")
            .Add("flagged", phoneConfirmed: true)
            .Add("admin", roles: [FlaggedAdministratorsDirectory.AdministratorRole])
            .Add("nobody");
        var dossier = await SeedDossierAsync(db, cast["owner"], restricted: true);
        var rule = QueryAuthorizers.AnyOf(
            QueryAuthorizers.WhenRole(
                cast.Role(FlaggedAdministratorsDirectory.AdministratorRole), QueryAuthorizers.Unrestricted<Dossier>()),
            QueryAuthorizers.WhenRole(
                FlaggedAdministratorsDirectory.AdministratorRole, QueryAuthorizers.Unrestricted<Dossier>()),
            QueryAuthorizers.ForUser<Dossier, Guid>((row, userId) => row.OwnerId == userId));

        directory.Should().BeOfType<FlaggedAdministratorsDirectory>("the application's directory is not replaced");
        var readers = await cast.Everyone
            .WhereAuthorizedFor(db.Dossiers.Where(row => row.Id == dossier.Id), rule, directory)
            .ToListAsync(ct);

        cast.Named(readers).Should().Equal("admin", "flagged", "owner");
    }

    [Fact]
    public async Task Via_TranslatesBothForms_AndItsReadersAgreeWithItsFilter() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReadersDbContext>();
        var directory = scope.ServiceProvider.GetRequiredService<IPrincipalDirectory<Guid>>();
        var cast = new Cast(db)
            .Add("owner")
            .Add("clerk", permissions: ["dossiers.read"])
            .Add("admin", roles: ["Admin"])
            .Add("nobody");
        var open = await SeedDossierAsync(db, cast["owner"], restricted: false);
        var restricted = await SeedDossierAsync(db, cast["owner"], restricted: true);
        var memos = new Dictionary<string, Memo> {
            ["open"] = Memo(new MemoLink { Kind = "dossier", RecordId = open.Id, DossierId = open.Id }),
            ["restricted"] = Memo(new MemoLink { Kind = "dossier", RecordId = restricted.Id, DossierId = restricted.Id }),
            ["unassigned"] = Memo(),
            // An invoice whose id equals a visible dossier's: only the kind keeps it out.
            ["invoice"] = Memo(new MemoLink { Kind = "invoice", RecordId = open.Id })
        };
        db.AddRange(memos.Values);
        await db.SaveChangesAsync(ct);
        var ids = memos.Values.Select(memo => memo.Id).ToArray();
        var dossierRule = QueryAuthorizers.AnyOf(
            QueryAuthorizers.WhenRole(cast.Role("Admin"), QueryAuthorizers.Unrestricted<Dossier>()),
            QueryAuthorizers.WhenPermission(
                "dossiers.read", QueryAuthorizers.Rows<Dossier>(dossier => !dossier.IsRestricted)),
            QueryAuthorizers.ForUser<Dossier, Guid>((dossier, userId) => dossier.OwnerId == userId));
        (string Form, IQueryAuthorizer<Memo> Rule)[] forms = [
            ("navigation", QueryAuthorizers.Via<Memo, Dossier>(
                memo => memo.Links.Where(link => link.DossierId != null).Select(link => link.Dossier!), dossierRule)),
            ("pointer", QueryAuthorizers.Via<Memo, Dossier>(
                db.Dossiers,
                (memo, dossier) => memo.Links.Any(link => link.Kind == "dossier" && link.RecordId == dossier.Id),
                dossierRule))
        ];
        (string Name, FakeCurrentUser User)[] principals = [
            ("owner", Principal(cast["owner"])),
            ("clerk", Principal(cast["clerk"], permissions: "dossiers.read")),
            ("admin", Principal(cast["admin"], [cast.Role("Admin")])),
            ("nobody", Principal(cast["nobody"]))
        ];

        foreach (var (form, rule) in forms) {
            var readersOf = new Dictionary<string, List<string>>();
            foreach (var (memoName, memo) in memos) {
                var readers = await cast.Everyone
                    .WhereAuthorizedFor(db.Set<Memo>().Where(row => row.Id == memo.Id), rule, directory)
                    .ToListAsync(ct);
                readersOf[memoName] = cast.Named(readers).ToList();
            }

            readersOf["open"].Should().Equal(["admin", "clerk", "owner"], "({0})", form);
            readersOf["restricted"].Should().Equal(["admin", "owner"], "({0})", form);
            readersOf["unassigned"].Should().BeEmpty("({0}) a memo needs an owner", form);
            readersOf["invoice"].Should().BeEmpty("({0}) the invoice is not a dossier", form);

            foreach (var (name, user) in principals) {
                var visible = await db.Set<Memo>().Where(memo => ids.Contains(memo.Id))
                    .WhereAuthorized(rule, user)
                    .Select(memo => memo.Id)
                    .ToListAsync(ct);
                var expected = memos.Where(memo => readersOf[memo.Key].Contains(name)).Select(memo => memo.Value.Id);
                visible.Should().BeEquivalentTo(expected, "({0}) both directions read one rule for {1}", form, name);
            }
        }
    }

    private static Memo Memo(params MemoLink[] links) {
        var memo = new Memo { Id = Guid.CreateVersion7() };
        foreach (var link in links) {
            link.Id = Guid.CreateVersion7();
            memo.Links.Add(link);
        }

        return memo;
    }

    private static FakeCurrentUser Principal(Guid id, string[]? roles = null, params string[] permissions) {
        return new FakeCurrentUser {
            IsAuthenticated = true,
            UserId = id.ToString(),
            Roles = roles ?? [],
            Claims = permissions.Select(permission => (Permission, permission)).ToArray()
        };
    }

    /// <summary>The Identity directory, plus the administrator role for every account the host flags.</summary>
    private sealed class FlaggedAdministratorsDirectory(ReadersDbContext db) : IPrincipalDirectory<Guid> {
        public const string AdministratorRole = "Administrators";

        private readonly IdentityPrincipalDirectory<ReadersDbContext, ApplicationRole, Guid> _stored = new(db);

        public IQueryable<PrincipalRole<Guid>> Roles => _stored.Roles.Concat(
            db.Set<ApplicationUser>()
                .Where(user => user.PhoneNumberConfirmed)
                .Select(user => new PrincipalRole<Guid> { PrincipalId = user.Id, Role = AdministratorRole }));

        public IQueryable<PrincipalClaim<Guid>> Claims => _stored.Claims;
    }
}

/// <summary>
/// Hand-written equivalent of the generated <c>[ResourceFilter&lt;Dossier&gt;(OwnerProperty = "OwnerId", Shared = true,
/// ResourceTypeName = "Dossier", BypassPermission = "dossiers.manage")]</c> authorizer, both directions, so this test
/// exercises the exact expressions the generator emits (the generator does not run as an analyzer in the test project).
/// </summary>
internal sealed class DossierAccess(IResourceGrantSource grants)
    : IQueryAuthorizer<Dossier>, IPrincipalQueryAuthorizer<Dossier> {
    public Expression<Func<Dossier, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation) {
        if (!user.IsAuthenticated) return _ => false;
        if (user.HasClaim("permission", "dossiers.manage")) return null;

        var ownerOk = Guid.TryParse(user.UserId, out var ownerKey);
        var op = operation.Name;
        var uid = user.UserId;
        var roles = user.Roles;
        var grantsQuery = grants.Grants;

        return dossier => (ownerOk && dossier.OwnerId == ownerKey)
                          || grantsQuery.Any(grant => grant.ResourceType == "Dossier"
                                                      && grant.ResourceId == dossier.Id.ToString()
                                                      && grant.Operation == op
                                                      && ((grant.PrincipalKind == "user" && grant.PrincipalId == uid)
                                                          || (grant.PrincipalKind == "role" &&
                                                              roles.Contains(grant.PrincipalId))));
    }

    public Expression<Func<Dossier, TKey, bool>> GetPrincipalRelation<TKey>(
        IPrincipalDirectory<TKey> directory, ResourceOperation operation)
        where TKey : notnull {
        if (typeof(TKey) == typeof(Guid))
            return (Expression<Func<Dossier, TKey, bool>>)(object)Relation(
                (IPrincipalDirectory<Guid>)(object)directory, operation);

        throw new NotSupportedException();
    }

    private Expression<Func<Dossier, Guid, bool>> Relation(IPrincipalDirectory<Guid> directory, ResourceOperation operation) {
        var bypassPermission = Queryable.Select(
            Queryable.Where(directory.Claims, c => c.Type == "permission" && c.Value == "dossiers.manage"),
            c => c.PrincipalId);
        var op = operation.Name;
        var grantsQuery = grants.Grants;
        var rolesQuery = directory.Roles;

        return (e, p) => e.OwnerId == p || Queryable.Contains(bypassPermission, p) || Queryable.Any(grantsQuery,
            g => g.ResourceType == "Dossier" && g.ResourceId == e.Id.ToString() && g.Operation == op &&
                 ((g.PrincipalKind == "user" && g.PrincipalId == p.ToString()) || (g.PrincipalKind == "role" &&
                     Queryable.Any(rolesQuery, r => r.PrincipalId == p && r.Role == g.PrincipalId))));
    }
}

/// <summary>A record assigned to dossiers (and other records) through its links.</summary>
public sealed class Memo {
    public Guid Id { get; set; }

    public List<MemoLink> Links { get; set; } = [];
}

/// <summary>
/// A memo's assignment: a <c>(kind, id)</c> pointer, plus a dossier foreign key for the navigation form of the same
/// assignment.
/// </summary>
public sealed class MemoLink {
    public Guid Id { get; set; }

    public Guid MemoId { get; set; }

    public string Kind { get; set; } = "";

    public Guid RecordId { get; set; }

    public Guid? DossierId { get; set; }

    public Dossier? Dossier { get; set; }
}

/// <summary>Maps <see cref="Dossier"/>, memos and their links, the Identity model and the resource-grants table.</summary>
public sealed class ReadersDbContext(DbContextOptions<ReadersDbContext> options) : DbContext(options) {
    public DbSet<Dossier> Dossiers => Set<Dossier>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<Dossier>(builder => {
            builder.ToTable("dossiers");
            builder.HasKey(dossier => dossier.Id);
        });
        modelBuilder.Entity<Memo>(builder => {
            builder.ToTable("memos");
            builder.HasKey(memo => memo.Id);
            builder.HasMany(memo => memo.Links).WithOne().HasForeignKey(link => link.MemoId);
        });
        modelBuilder.Entity<MemoLink>(builder => {
            builder.ToTable("memo_links");
            builder.HasKey(link => link.Id);
            builder.HasOne(link => link.Dossier).WithMany().HasForeignKey(link => link.DossierId);
        });
        modelBuilder.ApplyElarionIdentity<ApplicationUser, ApplicationRole, Guid>();
        modelBuilder.ApplyElarionResourceGrants();
    }
}

/// <summary>Starts a disposable PostgreSQL container and creates the schema once.</summary>
public sealed class PrincipalDirectoryFixture : IAsyncLifetime {
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
        await using var context = new ReadersDbContext(
            new DbContextOptionsBuilder<ReadersDbContext>().UseNpgsql(ConnectionString).Options);
        await context.Database.EnsureCreatedAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        await TestContainerLifecycle.Shared.DisposeAsync(_container);
    }
}
