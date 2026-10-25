using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Elarion.Abstractions.Caching;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.MultiTenancy;
using Elarion.Caching;
using Elarion.MultiTenancy;
using Elarion.Tests.MultiTenancy;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Caching;

/// <summary>
/// Tenant isolation of <see cref="HybridHandlerCache"/> once ambient tenant scoping (ADR-0075) is registered: a
/// cache hit never runs the handler, so the tenant read filter never runs either, and the physical key and tags
/// must carry the tenant themselves. Invalidation evicts within the tenant (and cross-tenant system-scope reads),
/// a system-scope or tenant-less invalidation evicts every tenant, and an unresolved tenant bypasses the cache.
/// Hosts without tenant scoping keep their exact pre-tenancy keys and tags.
/// </summary>
public sealed class HybridHandlerCacheTenantTests {
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GlobalScopedEntry_IsNeverServedToAnotherTenant() {
        await using var provider = CreateProvider(new MutableCurrentUser());
        var factoryCalls = 0;

        var forA = await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "for-a";
        });
        var forB = await ReadAsync(provider, InTenant(TenantB), HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "for-b";
        });
        var againForA = await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "unused";
        });

        forA.Should().Be("for-a");
        forB.Should().Be("for-b", "tenant B must never observe tenant A's cached entry");
        againForA.Should().Be("for-a", "the entry is still shared by every caller inside tenant A");
        factoryCalls.Should().Be(2);
    }

    [Fact]
    public async Task CurrentUserScopedEntry_IsNotSharedAcrossTheSameUsersTenants() {
        await using var provider = CreateProvider(new MutableCurrentUser { UserId = "member-of-both" });
        var factoryCalls = 0;

        var inA = await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.CurrentUser, () => {
            factoryCalls++;
            return "in-a";
        });
        // The same user switches tenant with ITenantContext.Scope, as a multi-tenant member does.
        var inB = await ReadAsync(provider, InTenant(TenantB), HandlerCacheScope.CurrentUser, () => {
            factoryCalls++;
            return "in-b";
        });

        inA.Should().Be("in-a");
        inB.Should().Be("in-b", "a user's entry in tenant A must not answer the same user's read in tenant B");
        factoryCalls.Should().Be(2);
    }

    [Fact]
    public async Task GlobalInvalidation_EvictsEveryUsersEntryInTheTenant_AndLeavesOtherTenantsCached() {
        var user = new MutableCurrentUser();
        await using var provider = CreateProvider(user);
        var factoryCalls = 0;

        user.UserId = "user-a";
        await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.CurrentUser, () => {
            factoryCalls++;
            return "a-of-tenant-a";
        });
        user.UserId = "user-c";
        await ReadAsync(provider, InTenant(TenantB), HandlerCacheScope.CurrentUser, () => {
            factoryCalls++;
            return "c-of-tenant-b";
        });
        factoryCalls.Should().Be(2);

        // Another user of tenant A runs the mutating command; [CacheInvalidate] defaults to Global.
        user.UserId = "user-b";
        await InvalidateAsync(provider, InTenant(TenantA), HandlerCacheScope.Global);

        user.UserId = "user-a";
        var refreshed = await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.CurrentUser, () => {
            factoryCalls++;
            return "a-of-tenant-a-2";
        });
        user.UserId = "user-c";
        var untouched = await ReadAsync(provider, InTenant(TenantB), HandlerCacheScope.CurrentUser, () => {
            factoryCalls++;
            return "unused";
        });

        refreshed.Should().Be("a-of-tenant-a-2", "a Global invalidation evicts every user's entry in the tenant");
        untouched.Should().Be("c-of-tenant-b", "one tenant's writes must not flush another tenant's cache");
        factoryCalls.Should().Be(3);
    }

    [Fact]
    public async Task SystemScope_HasItsOwnNamespace() {
        await using var provider = CreateProvider(new MutableCurrentUser());
        var factoryCalls = 0;

        var spanning = await ReadAsync(provider, SystemScope, HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "every-tenant";
        });
        var forA = await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "for-a";
        });
        var spanningAgain = await ReadAsync(provider, SystemScope, HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "unused";
        });

        spanning.Should().Be("every-tenant");
        forA.Should().Be("for-a", "a cross-tenant result must never answer a single tenant's read");
        spanningAgain.Should().Be("every-tenant");
        factoryCalls.Should().Be(2);
    }

    [Fact]
    public async Task TenantInvalidation_EvictsSystemScopeEntries() {
        await using var provider = CreateProvider(new MutableCurrentUser());
        var factoryCalls = 0;

        await ReadAsync(provider, SystemScope, HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "every-tenant";
        });

        // A write inside tenant A changes data a system-scope read spans.
        await InvalidateAsync(provider, InTenant(TenantA), HandlerCacheScope.Global);

        var spanning = await ReadAsync(provider, SystemScope, HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "every-tenant-2";
        });
        spanning.Should().Be("every-tenant-2");
        factoryCalls.Should().Be(2);
    }

    [Fact]
    public async Task SystemScopeInvalidation_EvictsEveryTenant() {
        var user = new MutableCurrentUser();
        await using var provider = CreateProvider(user);
        var factoryCalls = 0;

        await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "for-a";
        });
        await ReadAsync(provider, InTenant(TenantB), HandlerCacheScope.CurrentUser, () => {
            factoryCalls++;
            return "for-b";
        });

        await InvalidateAsync(provider, SystemScope, HandlerCacheScope.Global);

        var forA = await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "for-a-2";
        });
        var forB = await ReadAsync(provider, InTenant(TenantB), HandlerCacheScope.CurrentUser, () => {
            factoryCalls++;
            return "for-b-2";
        });

        forA.Should().Be("for-a-2");
        forB.Should().Be("for-b-2");
        factoryCalls.Should().Be(4);
    }

    [Fact]
    public async Task UnresolvedTenant_BypassesTheCache() {
        await using var provider = CreateProvider(new MutableCurrentUser());
        var factoryCalls = 0;

        await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "for-a";
        });
        var first = await ReadAsync(provider, Unresolved, HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "no-tenant-1";
        });
        var second = await ReadAsync(provider, Unresolved, HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "no-tenant-2";
        });

        first.Should().Be("no-tenant-1", "a caller with no tenant must never see a tenant's entry");
        second.Should().Be("no-tenant-2", "nothing is stored for a caller with no tenant");
        factoryCalls.Should().Be(3);
    }

    [Fact]
    public async Task UnresolvedTenantInvalidation_EvictsEveryTenant() {
        await using var provider = CreateProvider(new MutableCurrentUser());
        var factoryCalls = 0;

        await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "for-a";
        });

        // A command with no tenant cannot write tenant-scoped rows, so what it changed is data any tenant reads.
        await InvalidateAsync(provider, Unresolved, HandlerCacheScope.Global);

        var forA = await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.Global, () => {
            factoryCalls++;
            return "for-a-2";
        });
        forA.Should().Be("for-a-2");
        factoryCalls.Should().Be(2);
    }

    [Fact]
    public async Task WithoutTenantScoping_PhysicalKeysAndTagsAreUnchanged() {
        var recording = new RecordingHybridCache();
        await using var provider = CreateProvider(new MutableCurrentUser { UserId = "user-a" }, recording,
            withTenantScoping: false);
        var user = Hash("user-a");

        await ReadAsync(provider, null, HandlerCacheScope.Global, () => "x");
        await ReadAsync(provider, null, HandlerCacheScope.CurrentUser, () => "x");
        await InvalidateAsync(provider, null, HandlerCacheScope.Global);
        await InvalidateAsync(provider, null, HandlerCacheScope.CurrentUser);

        recording.Entries.Should().SatisfyRespectively(
            global => {
                global.Key.Should().Be("scope-test:scope:global:request:1");
                global.Tags.Should().Equal("handler-cache:global:tag:clients");
            },
            perUser => {
                perUser.Key.Should().Be($"scope-test:scope:user:{user}:request:1");
                perUser.Tags.Should().Equal(
                    $"handler-cache:user:{user}:tag:clients",
                    "handler-cache:global:tag:clients");
            });
        recording.RemovedTags.Should().Equal(
            "handler-cache:global:tag:clients",
            $"handler-cache:user:{user}:tag:clients");
    }

    [Fact]
    public async Task WithTenantScoping_TheTenantIsPartOfThePhysicalKeyAndTags() {
        var recording = new RecordingHybridCache();
        await using var provider = CreateProvider(new MutableCurrentUser { UserId = "user-a" }, recording);
        var user = Hash("user-a");
        var tenant = Hash(TenantA);

        await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.Global, () => "x");
        await ReadAsync(provider, InTenant(TenantA), HandlerCacheScope.CurrentUser, () => "x");
        await ReadAsync(provider, SystemScope, HandlerCacheScope.Global, () => "x");
        await InvalidateAsync(provider, InTenant(TenantA), HandlerCacheScope.Global);
        await InvalidateAsync(provider, InTenant(TenantA), HandlerCacheScope.CurrentUser);
        await InvalidateAsync(provider, SystemScope, HandlerCacheScope.Global);

        recording.Entries.Should().SatisfyRespectively(
            global => {
                global.Key.Should().Be($"scope-test:tenant:{tenant}:scope:global:request:1");
                global.Tags.Should().Equal(
                    "handler-cache:global:tag:clients",
                    $"handler-cache:tenant:{tenant}:global:tag:clients");
            },
            perUser => {
                perUser.Key.Should().Be($"scope-test:tenant:{tenant}:scope:user:{user}:request:1");
                perUser.Tags.Should().Equal(
                    $"handler-cache:user:{user}:tag:clients",
                    "handler-cache:global:tag:clients",
                    $"handler-cache:tenant:{tenant}:user:{user}:tag:clients",
                    $"handler-cache:tenant:{tenant}:global:tag:clients");
            },
            spanning => {
                spanning.Key.Should().Be("scope-test:tenant:system:scope:global:request:1");
                spanning.Tags.Should().Equal(
                    "handler-cache:global:tag:clients",
                    "handler-cache:tenant:system:global:tag:clients");
            });
        recording.RemovedTags.Should().Equal(
            $"handler-cache:tenant:{tenant}:global:tag:clients",
            "handler-cache:tenant:system:global:tag:clients",
            $"handler-cache:tenant:{tenant}:user:{user}:tag:clients",
            $"handler-cache:tenant:system:user:{user}:tag:clients",
            "handler-cache:global:tag:clients");
    }

    private static Func<ITenantContext, IDisposable?> InTenant(string tenantId) {
        return tenant => tenant.Scope(tenantId);
    }

    private static readonly Func<ITenantContext, IDisposable?> SystemScope = tenant => tenant.SystemScope();

    private static readonly Func<ITenantContext, IDisposable?> Unresolved = _ => null;

    /// <summary>One request: a fresh DI scope and tenant context, entered the way the caller enters it.</summary>
    private static async Task<string> ReadAsync(
        ServiceProvider provider,
        Func<ITenantContext, IDisposable?>? enter,
        HandlerCacheScope scope,
        Func<string> factory) {
        await using var request = provider.CreateAsyncScope();
        using var entered = enter?.Invoke(request.ServiceProvider.GetRequiredService<ITenantContext>());
        var cache = request.ServiceProvider.GetRequiredService<IHandlerCache>();

        return await cache.GetOrCreateAsync(
            new Policy(scope),
            new Request { Id = 1 },
            _ => ValueTask.FromResult(factory()),
            Ct);
    }

    private static async Task InvalidateAsync(
        ServiceProvider provider,
        Func<ITenantContext, IDisposable?>? enter,
        HandlerCacheScope scope) {
        await using var request = provider.CreateAsyncScope();
        using var entered = enter?.Invoke(request.ServiceProvider.GetRequiredService<ITenantContext>());
        var cache = request.ServiceProvider.GetRequiredService<IHandlerCache>();

        await cache.InvalidateAsync(new InvalidationPolicy(scope), Ct);
    }

    private static ServiceProvider CreateProvider(
        MutableCurrentUser user,
        HybridCache? hybridCache = null,
        bool withTenantScoping = true) {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(user);
        // Registered first so AddHybridCache's TryAdd keeps it.
        if (hybridCache is not null) services.AddSingleton(hybridCache);
        if (withTenantScoping) {
            // No resolved tenant by default: each request enters its tenant explicitly, or stays unresolved.
            services.AddSingleton<ITenantResolver>(new FixedTenantResolver(null));
            services.AddElarionTenantScoping();
        }

        services.AddElarionHandlerCaching();
        return services.BuildServiceProvider();
    }

    private static string Hash(string value) {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed record Request {
        public int Id { get; init; }
    }

    private sealed class Policy(HandlerCacheScope scope) : IHandlerCachePolicy<Request> {
        public string KeyPrefix => "scope-test";
        public TimeSpan Expiration => TimeSpan.FromMinutes(1);
        public HandlerCacheScope Scope => scope;
        public IReadOnlyList<string> Tags { get; } = ["clients"];

        public string CreateKey(Request request) {
            return request.Id.ToString();
        }
    }

    private sealed class InvalidationPolicy(HandlerCacheScope scope) : IHandlerCacheInvalidationPolicy {
        public HandlerCacheScope Scope => scope;
        public IReadOnlyList<string> Tags { get; } = ["clients"];
    }

    private sealed class MutableCurrentUser : ICurrentUser {
        public string UserId { get; set; } = "user-a";
        public string? Email => null;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => true;

        public bool IsInRole(string role) {
            return false;
        }
    }

    /// <summary>Records the physical key and tags of every entry and every removed tag, and stores nothing.</summary>
    private sealed class RecordingHybridCache : HybridCache {
        public List<(string Key, string[] Tags)> Entries { get; } = [];

        public List<string> RemovedTags { get; } = [];

        public override async ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default) {
            Entries.Add((key, tags?.ToArray() ?? []));
            return await factory(state, cancellationToken);
        }

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default) {
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) {
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) {
            RemovedTags.Add(tag);
            return ValueTask.CompletedTask;
        }
    }
}
