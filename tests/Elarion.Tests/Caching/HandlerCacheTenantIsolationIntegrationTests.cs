using AwesomeAssertions;
using Elarion.Abstractions.Caching;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.MultiTenancy;
using Elarion.Caching;
using Elarion.EntityFrameworkCore.MultiTenancy;
using Elarion.MultiTenancy;
using Elarion.Tests.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Caching;

/// <summary>
/// The handler cache against real PostgreSQL tenant scoping: a cached read of a tenant-scoped entity must answer
/// each tenant with its own rows. A cache hit skips the handler, and with it the tenant query filter, so only the
/// physical key can keep tenants apart. Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HandlerCacheTenantIsolationIntegrationTests(TenantScopingFixture fixture)
    : IClassFixture<TenantScopingFixture> {
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task GlobalScopedRead_OfTenantScopedRows_AnswersEachTenantWithItsOwnRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var provider = CreateProvider("user-a");
        var name = await SeedAcrossTenantsAsync(provider, ct);

        var seenByA = await ReadOwnersAsync(provider, TenantA, name, HandlerCacheScope.Global, ct);
        var seenByB = await ReadOwnersAsync(provider, TenantB, name, HandlerCacheScope.Global, ct);

        seenByA.Should().Be(TenantA);
        seenByB.Should().Be(TenantB, "tenant B must not be served tenant A's cached rows");
    }

    [Fact]
    public async Task CurrentUserScopedRead_ForAUserInTwoTenants_AnswersEachTenantWithItsOwnRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var provider = CreateProvider("member-of-both");
        var name = await SeedAcrossTenantsAsync(provider, ct);

        var inA = await ReadOwnersAsync(provider, TenantA, name, HandlerCacheScope.CurrentUser, ct);
        var inB = await ReadOwnersAsync(provider, TenantB, name, HandlerCacheScope.CurrentUser, ct);

        inA.Should().Be(TenantA);
        inB.Should().Be(TenantB, "switching tenant must not replay the user's entry from the other tenant");
    }

    /// <summary>
    /// The read a <c>[Cacheable]</c> handler would make: it says nothing about tenancy and relies on the
    /// model's tenant filter, exactly as a generated <c>CacheDecorator</c> runs it on a miss.
    /// </summary>
    private static async Task<string> ReadOwnersAsync(
        ServiceProvider provider,
        string tenantId,
        string name,
        HandlerCacheScope scope,
        CancellationToken ct) {
        await using var request = provider.CreateAsyncScope();
        using var _ = request.ServiceProvider.GetRequiredService<ITenantContext>().Scope(tenantId);
        var db = request.ServiceProvider.GetRequiredService<TenantDbContext>();
        var cache = request.ServiceProvider.GetRequiredService<IHandlerCache>();

        return await cache.GetOrCreateAsync(
            new NotesByNamePolicy(scope),
            name,
            async token => {
                var owners = await db.Notes
                    .Where(note => note.Name == name)
                    .Select(note => note.TenantId)
                    .ToListAsync(token);
                return string.Join(",", owners.Order());
            },
            ct);
    }

    private static async Task<string> SeedAcrossTenantsAsync(ServiceProvider provider, CancellationToken ct) {
        var name = $"cached-{Guid.CreateVersion7()}";
        foreach (var tenantId in new[] { TenantA, TenantB }) {
            await using var scope = provider.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<ITenantContext>().Scope(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            db.Notes.Add(new Note { Name = name });
            await db.SaveChangesAsync(ct);
        }

        return name;
    }

    private ServiceProvider CreateProvider(string userId) {
        var services = new ServiceCollection();
        services.AddDbContext<TenantDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddSingleton<ITenantResolver>(new FixedTenantResolver(null));
        services.AddElarionTenantScoping();
        services.AddElarionTenantScopingEntityFrameworkCore<TenantDbContext>();
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(userId));
        services.AddElarionHandlerCaching();
        return services.BuildServiceProvider();
    }

    private sealed class NotesByNamePolicy(HandlerCacheScope scope) : IHandlerCachePolicy<string> {
        public string KeyPrefix => "notes-by-name";
        public TimeSpan Expiration => TimeSpan.FromMinutes(1);
        public HandlerCacheScope Scope => scope;
        public IReadOnlyList<string> Tags { get; } = ["notes"];

        public string CreateKey(string request) {
            return request;
        }
    }

    private sealed class FixedCurrentUser(string userId) : ICurrentUser {
        public string UserId => userId;
        public string? Email => null;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => true;

        public bool IsInRole(string role) {
            return false;
        }
    }
}
