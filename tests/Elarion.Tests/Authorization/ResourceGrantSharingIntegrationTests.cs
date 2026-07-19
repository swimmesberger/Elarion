using System.Linq;
using System.Linq.Expressions;
using AwesomeAssertions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Paging;
using Elarion.Authorization.EntityFrameworkCore;
using Elarion.Paging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Authorization;

/// <summary>
/// End-to-end verification that a <c>[ResourceFilter(Shared = true)]</c>-shaped predicate pushes a correlated
/// <c>EXISTS</c> over the resource-grants table into SQL, so a row shared with one of the caller's <b>roles</b>
/// (e.g. a contact shared with the "Hausmeister" role) is visible — filtered by the database, with correct
/// pagination. Uses a hand-written authorizer matching the generator's output, since the generator does not run
/// as a build analyzer in the test project. Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ResourceGrantSharingIntegrationTests(ResourceGrantSharingFixture fixture)
    : IClassFixture<ResourceGrantSharingFixture> {
    private static readonly Guid OwnerB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task RoleSharedContact_IsVisibleToUserInThatRole_AndFiltersInSql() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var services = new ServiceCollection();
        services.AddDbContext<ContactsDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddElarionResourceAuthorization<ContactsDbContext>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContactsDbContext>();
        var grantStore = scope.ServiceProvider.GetRequiredService<IResourceGrantStore>();
        var grantSource = scope.ServiceProvider.GetRequiredService<IResourceGrantSource>();

        var shared = await SeedContactAsync(db, OwnerB, "Handwerker shared with Hausmeister", ct);
        var privateB = await SeedContactAsync(db, OwnerB, "Private B contact", ct);

        // Share the one contact with the "Hausmeister" role for read.
        await grantStore.GrantAsync(
            new ResourceGrant("Contact", shared.ToString(), ResourcePrincipal.Role("Hausmeister"),
                ResourceOperation.Read), ct);

        var hausmeister = new FakeCurrentUser {
            IsAuthenticated = true,
            UserId = Guid.NewGuid().ToString(),
            Roles = ["Hausmeister"]
        };
        var authorizer = new ContactAccess(grantSource);

        var query = db.Contacts.WhereAuthorized(authorizer, hausmeister);

        // The grant check is pushed into SQL as a correlated EXISTS, not evaluated in memory.
        query.ToQueryString().Should().Contain("EXISTS");

        var page = await query.ToOffsetPageAsync(
            new OffsetPageRequest { Page = 1, Size = 50 },
            c => c.Id,
            SortMap<Contact>.CreateBuilder("id", c => c.Id).Build(),
            cancellationToken: ct);

        page.Items.Should().Contain(shared);
        page.Items.Should().NotContain(privateB);
    }

    [Fact]
    public async Task NonMemberUser_DoesNotSeeRoleSharedContact() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var services = new ServiceCollection();
        services.AddDbContext<ContactsDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddElarionResourceAuthorization<ContactsDbContext>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContactsDbContext>();
        var grantStore = scope.ServiceProvider.GetRequiredService<IResourceGrantStore>();
        var grantSource = scope.ServiceProvider.GetRequiredService<IResourceGrantSource>();

        var shared = await SeedContactAsync(db, OwnerB, "Shared with Hausmeister only", ct);
        await grantStore.GrantAsync(
            new ResourceGrant("Contact", shared.ToString(), ResourcePrincipal.Role("Hausmeister"),
                ResourceOperation.Read), ct);

        var outsider = new FakeCurrentUser {
            IsAuthenticated = true,
            UserId = Guid.NewGuid().ToString(),
            Roles = ["Tenant"]
        };

        var page = await db.Contacts
            .WhereAuthorized(new ContactAccess(grantSource), outsider)
            .ToOffsetPageAsync(
                new OffsetPageRequest { Page = 1, Size = 50 },
                c => c.Id,
                SortMap<Contact>.CreateBuilder("id", c => c.Id).Build(),
                cancellationToken: ct);

        page.Items.Should().NotContain(shared);
    }

    [Fact]
    public async Task PointCheck_GrantResourceAuthorizer_AuthorizesRoleSharedResource() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var services = new ServiceCollection();
        services.AddDbContext<ContactsDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddElarionResourceAuthorization<ContactsDbContext>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContactsDbContext>();
        var grantStore = scope.ServiceProvider.GetRequiredService<IResourceGrantStore>();
        // AddElarionResourceAuthorization registers the grants-backed IResourceAuthorizer.
        var resourceAuthorizer = scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>();

        var shared = await SeedContactAsync(db, OwnerB, "Point-check shared with Hausmeister", ct);
        await grantStore.GrantAsync(
            new ResourceGrant("Contact", shared.ToString(), ResourcePrincipal.Role("Hausmeister"),
                ResourceOperation.Read), ct);

        var member = new FakeCurrentUser {
            IsAuthenticated = true, UserId = Guid.NewGuid().ToString(), Roles = ["Hausmeister"]
        };
        var outsider = new FakeCurrentUser {
            IsAuthenticated = true, UserId = Guid.NewGuid().ToString(), Roles = ["Other"]
        };

        var memberAllowed = await resourceAuthorizer.AuthorizeResourceAsync(
            new ResourceAuthorizationContext(member, typeof(Contact), "Contact", ResourceOperation.Read, shared), ct);
        var outsiderAllowed = await resourceAuthorizer.AuthorizeResourceAsync(
            new ResourceAuthorizationContext(outsider, typeof(Contact), "Contact", ResourceOperation.Read, shared), ct);

        memberAllowed.Should().BeTrue();
        outsiderAllowed.Should().BeFalse();
    }

    private static async Task<Guid> SeedContactAsync(ContactsDbContext db, Guid ownerId, string name,
        CancellationToken ct) {
        var contact = new Contact { Id = Guid.NewGuid(), OwnerId = ownerId, Name = name };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync(ct);
        return contact.Id;
    }
}

/// <summary>A contact owned by a user; the demo resource for role-based sharing.</summary>
public sealed class Contact {
    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>Integration context mapping <see cref="Contact"/> and the Elarion resource-grants table.</summary>
public sealed class ContactsDbContext(DbContextOptions<ContactsDbContext> options) : DbContext(options) {
    public DbSet<Contact> Contacts => Set<Contact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<Contact>(builder => {
            builder.ToTable("contacts");
            builder.HasKey(contact => contact.Id);
        });
        modelBuilder.ApplyElarionResourceGrants();
    }
}

/// <summary>
/// Hand-written equivalent of the generated <c>[ResourceFilter&lt;Contact&gt;(OwnerProperty = "OwnerId",
/// Shared = true, ResourceTypeName = "Contact")]</c> authorizer, so this test exercises the exact predicate
/// shape the generator emits.
/// </summary>
internal sealed class ContactAccess(IResourceGrantSource grants) : IQueryAuthorizer<Contact> {
    public Expression<Func<Contact, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation) {
        if (!user.IsAuthenticated) return _ => false;

        var ownerOk = Guid.TryParse(user.UserId, out var ownerKey);
        var op = operation.Name;
        var uid = user.UserId;
        var roles = user.Roles;
        var grantsQuery = grants.Grants;

        return contact => (ownerOk && contact.OwnerId == ownerKey)
                          || grantsQuery.Any(grant => grant.ResourceType == "Contact"
                                                      && grant.ResourceId == contact.Id.ToString()
                                                      && grant.Operation == op
                                                      && ((grant.PrincipalKind == "user" && grant.PrincipalId == uid)
                                                          || (grant.PrincipalKind == "role" &&
                                                              roles.Contains(grant.PrincipalId))));
    }
}

/// <summary>Starts a disposable PostgreSQL container and creates the contacts + grants schema once.</summary>
public sealed class ResourceGrantSharingFixture : IAsyncLifetime {
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
        await using var context = new ContactsDbContext(
            new DbContextOptionsBuilder<ContactsDbContext>().UseNpgsql(ConnectionString).Options);
        await context.Database.EnsureCreatedAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) await _container.DisposeAsync();
    }
}
