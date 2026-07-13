using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Caching;
using Elarion.Abstractions.Identity;
using Elarion.Caching;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Caching;

/// <summary>
/// Scope semantics of <see cref="HybridHandlerCache"/>: user-scoped entries are isolated per user but carry
/// the global tag namespace, so the default attribute pairing — <c>[Cacheable]</c> (CurrentUser) with
/// <c>[CacheInvalidate]</c> (Global) — actually evicts, while a CurrentUser-scoped invalidation clears only
/// the invoking user's entries. Also covers the failed-<c>Result</c> never-cached control flow.
/// </summary>
public sealed class HybridHandlerCacheScopeTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GlobalInvalidation_EvictsAnotherUsersUserScopedEntry_TheDefaultAttributePairing() {
        await using var provider = CreateProvider(out var user);
        var cache = provider.GetRequiredService<IHandlerCache>();
        var factoryCalls = 0;

        user.UserId = "user-a";
        var first = await GetOrCreateAsync(cache, () => { factoryCalls++; return "fresh-a"; });
        first.Should().Be("fresh-a");
        factoryCalls.Should().Be(1);

        // A different user runs the mutating command; its [CacheInvalidate] default scope is Global.
        user.UserId = "user-b";
        await cache.InvalidateAsync(new InvalidationPolicy(HandlerCacheScope.Global), Ct);

        user.UserId = "user-a";
        await GetOrCreateAsync(cache, () => { factoryCalls++; return "fresh-a-2"; });
        factoryCalls.Should().Be(2, "the global invalidation must evict user A's user-scoped entry");
    }

    [Fact]
    public async Task UserScopedEntries_AreNeverServedToAnotherUser() {
        await using var provider = CreateProvider(out var user);
        var cache = provider.GetRequiredService<IHandlerCache>();
        var factoryCalls = 0;

        user.UserId = "user-a";
        var forA = await GetOrCreateAsync(cache, () => { factoryCalls++; return "for-a"; });

        user.UserId = "user-b";
        var forB = await GetOrCreateAsync(cache, () => { factoryCalls++; return "for-b"; });

        forA.Should().Be("for-a");
        forB.Should().Be("for-b", "user B must never observe user A's cached entry");
        factoryCalls.Should().Be(2);
    }

    [Fact]
    public async Task CurrentUserInvalidation_ClearsOnlyTheInvokingUsersEntries() {
        await using var provider = CreateProvider(out var user);
        var cache = provider.GetRequiredService<IHandlerCache>();
        var factoryCalls = 0;

        user.UserId = "user-a";
        await GetOrCreateAsync(cache, () => { factoryCalls++; return "for-a"; });
        user.UserId = "user-b";
        await GetOrCreateAsync(cache, () => { factoryCalls++; return "for-b"; });
        factoryCalls.Should().Be(2);

        // User B invalidates with an explicit CurrentUser scope: only B's namespace is cleared.
        await cache.InvalidateAsync(new InvalidationPolicy(HandlerCacheScope.CurrentUser), Ct);

        var forB = await GetOrCreateAsync(cache, () => { factoryCalls++; return "for-b-2"; });
        forB.Should().Be("for-b-2");
        factoryCalls.Should().Be(3);

        user.UserId = "user-a";
        var forA = await GetOrCreateAsync(cache, () => { factoryCalls++; return "unused"; });
        forA.Should().Be("for-a", "user A's entry must survive user B's CurrentUser-scoped invalidation");
        factoryCalls.Should().Be(3);
    }

    [Fact]
    public async Task CurrentUserInvalidation_LeavesGlobalEntriesIntact() {
        await using var provider = CreateProvider(out var user);
        var cache = provider.GetRequiredService<IHandlerCache>();
        var factoryCalls = 0;

        user.UserId = "user-a";
        await GetOrCreateAsync(cache, () => { factoryCalls++; return "shared"; }, HandlerCacheScope.Global);

        await cache.InvalidateAsync(new InvalidationPolicy(HandlerCacheScope.CurrentUser), Ct);

        var again = await GetOrCreateAsync(cache, () => { factoryCalls++; return "unused"; }, HandlerCacheScope.Global);
        again.Should().Be("shared");
        factoryCalls.Should().Be(1);
    }

    [Fact]
    public async Task FailedResult_IsReturned_ButNeverCached() {
        await using var provider = CreateProvider(out _);
        var cache = provider.GetRequiredService<IHandlerCache>();
        var factoryCalls = 0;
        var policy = new Policy(HandlerCacheScope.Global);

        var first = await cache.GetOrCreateAsync(
            policy,
            new Request { Id = 1 },
            _ => { factoryCalls++; return ValueTask.FromResult(Result<string>.Failure(AppError.NotFound("missing"))); },
            Ct);
        var second = await cache.GetOrCreateAsync(
            policy,
            new Request { Id = 1 },
            _ => { factoryCalls++; return ValueTask.FromResult(Result<string>.Failure(AppError.NotFound("still missing"))); },
            Ct);

        first.IsSuccess.Should().BeFalse();
        second.IsSuccess.Should().BeFalse();
        second.Error.Message.Should().Be("still missing", "the second call must re-execute the factory, not replay a cached failure");
        factoryCalls.Should().Be(2);
    }

    [Fact]
    public async Task PayloadPolicy_FailedResult_IsNeverCached_ButALaterSuccessIs() {
        await using var provider = CreateProvider(out _);
        var cache = provider.GetRequiredService<IHandlerCache>();
        var factoryCalls = 0;
        var policy = new PayloadPolicy();

        var failed = await cache.GetOrCreateAsync(
            policy,
            new Request { Id = 1 },
            _ => { factoryCalls++; return ValueTask.FromResult(Result<string>.Failure(AppError.BusinessRule("no"))); },
            Ct);
        var succeeded = await cache.GetOrCreateAsync(
            policy,
            new Request { Id = 1 },
            _ => { factoryCalls++; return ValueTask.FromResult(Result<string>.Success("ok")); },
            Ct);
        var replayed = await cache.GetOrCreateAsync(
            policy,
            new Request { Id = 1 },
            _ => { factoryCalls++; return ValueTask.FromResult(Result<string>.Success("unused")); },
            Ct);

        failed.IsSuccess.Should().BeFalse();
        succeeded.IsSuccess.Should().BeTrue();
        replayed.Value.Should().Be("ok", "the success must be cached while the earlier failure was not");
        factoryCalls.Should().Be(2);
    }

    private static async Task<string> GetOrCreateAsync(
        IHandlerCache cache,
        Func<string> factory,
        HandlerCacheScope scope = HandlerCacheScope.CurrentUser) =>
        await cache.GetOrCreateAsync(
            new Policy(scope),
            new Request { Id = 1 },
            _ => ValueTask.FromResult(factory()),
            Ct);

    private static ServiceProvider CreateProvider(out MutableCurrentUser user) {
        user = new MutableCurrentUser();
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(user);
        services.AddElarionHandlerCaching();
        return services.BuildServiceProvider();
    }

    private sealed record Request {
        public int Id { get; init; }
    }

    private sealed class Policy(HandlerCacheScope scope) : IHandlerCachePolicy<Request> {
        public string KeyPrefix => "scope-test";
        public TimeSpan Expiration => TimeSpan.FromMinutes(1);
        public HandlerCacheScope Scope => scope;
        public IReadOnlyList<string> Tags { get; } = ["clients"];
        public string CreateKey(Request request) => request.Id.ToString();
    }

    private sealed class PayloadPolicy : IHandlerCachePayloadPolicy<Request, Result<string>> {
        public string KeyPrefix => "payload-scope-test";
        public TimeSpan Expiration => TimeSpan.FromMinutes(1);
        public HandlerCacheScope Scope => HandlerCacheScope.Global;
        public IReadOnlyList<string> Tags { get; } = ["clients"];
        public string CreateKey(Request request) => request.Id.ToString();

        public string Serialize(Result<string> response, JsonSerializerOptions options) => response.Value;

        public Result<string> Deserialize(string payload, JsonSerializerOptions options) =>
            Result<string>.Success(payload);
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
        public bool IsInRole(string role) => false;
    }
}
