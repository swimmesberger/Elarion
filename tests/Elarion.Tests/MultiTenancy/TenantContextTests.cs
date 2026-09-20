using AwesomeAssertions;
using Elarion.Abstractions.MultiTenancy;
using Elarion.MultiTenancy;
using Elarion.Tests.Authorization;
using Xunit;

namespace Elarion.Tests.MultiTenancy;

public sealed class TenantContextTests {
    [Fact]
    public void TenantId_ResolvesOnceAndCachesForTheScope() {
        var resolver = new CountingResolver("tenant-a");
        var context = new TenantContext(resolver);

        _ = context.TenantId;
        _ = context.TenantId;

        context.TenantId.Should().Be("tenant-a");
        resolver.Calls.Should().Be(1);
    }

    [Fact]
    public void TenantId_CachesAnAbsentTenantToo() {
        var resolver = new CountingResolver(null);
        var context = new TenantContext(resolver);

        _ = context.TenantId;
        context.TenantId.Should().BeNull();
        resolver.Calls.Should().Be(1);
    }

    [Fact]
    public void Scope_OverridesTheResolverAndRestoresOnDispose() {
        var context = new TenantContext(new CountingResolver("resolved"));

        using (context.Scope("explicit")) {
            context.TenantId.Should().Be("explicit");
        }

        context.TenantId.Should().Be("resolved");
    }

    [Fact]
    public void Scope_Nests_AndRestoresTheEnclosingTenant() {
        var context = new TenantContext(new CountingResolver(null));

        using (context.Scope("outer")) {
            using (context.Scope("inner")) {
                context.TenantId.Should().Be("inner");
            }

            context.TenantId.Should().Be("outer");
        }

        context.TenantId.Should().BeNull();
    }

    [Fact]
    public void Scope_RejectsABlankTenant() {
        var context = new TenantContext(new CountingResolver(null));

        var act = () => context.Scope("  ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SystemScope_IsOffByDefault_AndNestsByDepth() {
        var context = new TenantContext(new CountingResolver("tenant-a"));

        context.IsSystemScope.Should().BeFalse();
        using (context.SystemScope()) {
            using (context.SystemScope()) {
                context.IsSystemScope.Should().BeTrue();
            }

            // The inner dispose must not end the outer declaration.
            context.IsSystemScope.Should().BeTrue();
        }

        context.IsSystemScope.Should().BeFalse();
    }

    [Fact]
    public void SystemScope_DisposingTwice_DoesNotEndAnEnclosingScope() {
        var context = new TenantContext(new CountingResolver(null));

        var outer = context.SystemScope();
        var inner = context.SystemScope();
        inner.Dispose();
        inner.Dispose();

        context.IsSystemScope.Should().BeTrue();
        outer.Dispose();
        context.IsSystemScope.Should().BeFalse();
    }

    [Fact]
    public void ClaimsResolver_ReadsTheConfiguredClaim() {
        var user = new FakeCurrentUser {
            IsAuthenticated = true, Claims = [("workspace", "ws-7")]
        };

        new ClaimsTenantResolver(user, new TenantScopingOptions { ClaimType = "workspace" })
            .Resolve().Should().Be("ws-7");
    }

    [Fact]
    public void ClaimsResolver_DefaultsToTheTenantClaim() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("tenant", "t-1")] };

        new ClaimsTenantResolver(user, new TenantScopingOptions()).Resolve().Should().Be("t-1");
    }

    [Fact]
    public void ClaimsResolver_UnauthenticatedPrincipalHasNoTenant() {
        var user = new FakeCurrentUser { IsAuthenticated = false, Claims = [("tenant", "t-1")] };

        new ClaimsTenantResolver(user, new TenantScopingOptions()).Resolve().Should().BeNull();
    }

    [Fact]
    public void ClaimsResolver_AmbiguousTenantClaimsDenyRatherThanPickOne() {
        var user = new FakeCurrentUser {
            IsAuthenticated = true, Claims = [("tenant", "t-1"), ("tenant", "t-2")]
        };

        new ClaimsTenantResolver(user, new TenantScopingOptions()).Resolve().Should().BeNull();
    }

    [Fact]
    public void ClaimsResolver_IgnoresABlankClaimValue() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("tenant", "   ")] };

        new ClaimsTenantResolver(user, new TenantScopingOptions()).Resolve().Should().BeNull();
    }

    private sealed class CountingResolver(string? tenantId) : ITenantResolver {
        public int Calls { get; private set; }

        public string? Resolve() {
            Calls++;
            return tenantId;
        }
    }
}
