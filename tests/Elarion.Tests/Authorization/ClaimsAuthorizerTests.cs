using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Authorization;

public sealed class ClaimsAuthorizerTests {
    private static ClaimsAuthorizer Create(
        FakeCurrentUser user,
        IEnumerable<IAuthorizationPolicy>? policies = null,
        AuthorizationOptions? options = null) =>
        new(user, policies ?? [], options ?? new AuthorizationOptions(), NullLogger<ClaimsAuthorizer>.Instance);

    private static AuthorizationRequirements Requirements(
        bool requireAuthenticated = false,
        IReadOnlyList<string>? permissions = null,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<RequireClaimAttribute>? claims = null,
        IReadOnlyList<string>? policies = null,
        bool allowAnonymous = false) =>
        new(allowAnonymous, requireAuthenticated, permissions ?? [], roles ?? [], claims ?? [], policies ?? []);

    [Fact]
    public async Task AllowAnonymousShortCircuits() {
        var authorizer = Create(new FakeCurrentUser { IsAuthenticated = false });

        var error = await authorizer.AuthorizeAsync(
            Requirements(allowAnonymous: true, permissions: ["x"]), null, TestContext.Current.CancellationToken);

        error.Should().BeNull();
    }

    [Fact]
    public async Task UsesConfiguredPermissionClaimType() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("perm", "tenants.read")] };
        var authorizer = Create(user, options: new AuthorizationOptions { PermissionClaimType = "perm" });

        var error = await authorizer.AuthorizeAsync(
            Requirements(permissions: ["tenants.read"]), null, TestContext.Current.CancellationToken);

        error.Should().BeNull();
    }

    [Fact]
    public async Task UnregisteredPolicyDeniesClosed() {
        var user = new FakeCurrentUser { IsAuthenticated = true };
        var authorizer = Create(user);

        var error = await authorizer.AuthorizeAsync(
            Requirements(policies: ["AtLeast21"]), null, TestContext.Current.CancellationToken);

        error!.Kind.Should().Be(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task RegisteredPolicyEvaluated() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("age", "25")] };
        var authorizer = Create(user, policies: [new AtLeast21Policy()]);

        var error = await authorizer.AuthorizeAsync(
            Requirements(policies: ["AtLeast21"]), new GuardedCommand(1), TestContext.Current.CancellationToken);

        error.Should().BeNull();
    }

    [Fact]
    public async Task RequireClaimPresenceOnly() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("tenant", "acme")] };
        var authorizer = Create(user);

        var present = await authorizer.AuthorizeAsync(
            Requirements(claims: [new RequireClaimAttribute("tenant")]), null, TestContext.Current.CancellationToken);
        present.Should().BeNull();

        var missing = await authorizer.AuthorizeAsync(
            Requirements(claims: [new RequireClaimAttribute("missing")]), null, TestContext.Current.CancellationToken);
        missing!.Kind.Should().Be(ErrorKind.Forbidden);
    }
}
