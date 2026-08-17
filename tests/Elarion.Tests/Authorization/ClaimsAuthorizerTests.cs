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
        IEnumerable<NamedAuthorizationPolicy>? policies = null,
        AuthorizationOptions? options = null,
        IResourceAuthorizer? resourceAuthorizer = null,
        IEnumerable<IGlobalAuthorizationRule>? globalRules = null) {
        return new ClaimsAuthorizer(user, policies ?? [], resourceAuthorizer ?? new StubResourceAuthorizer(),
            options ?? new AuthorizationOptions(), NullLogger<ClaimsAuthorizer>.Instance, globalRules);
    }

    private static AuthorizationRequirements Requirements(
        bool requireAuthenticated = false,
        IReadOnlyList<string>? permissions = null,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<RequireClaimAttribute>? claims = null,
        IReadOnlyList<string>? policies = null,
        bool allowAnonymous = false,
        IReadOnlyList<ResourceRequirement>? resources = null) {
        return new AuthorizationRequirements(allowAnonymous, requireAuthenticated, permissions ?? [], roles ?? [],
            claims ?? [], policies ?? [],
            resources ?? []);
    }

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
    public async Task ForbiddenMessageIsGenericByDefault() {
        var user = new FakeCurrentUser { IsAuthenticated = true };
        var authorizer = Create(user);

        var error = await authorizer.AuthorizeAsync(
            Requirements(permissions: ["tenants.read"]), null, TestContext.Current.CancellationToken);

        error!.Kind.Should().Be(ErrorKind.Forbidden);
        // The default deliberately omits the unmet requirement so a forbidden caller cannot
        // probe the permission vocabulary; ForbiddenMessageFormat opts back into the detail.
        error.Message.Should().Be("Access denied.");
        error.Message.Should().NotContain("tenants.read");
    }

    [Fact]
    public async Task ForbiddenMessageFormatRestoresRequirementDetail() {
        var user = new FakeCurrentUser { IsAuthenticated = true };
        var authorizer = Create(user, options: new AuthorizationOptions {
            ForbiddenMessageFormat = "Missing required permission: {0}"
        });

        var error = await authorizer.AuthorizeAsync(
            Requirements(permissions: ["tenants.read"]), null, TestContext.Current.CancellationToken);

        error!.Kind.Should().Be(ErrorKind.Forbidden);
        error.Message.Should().Be("Missing required permission: tenants.read");
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
        var authorizer = Create(user, [new NamedAuthorizationPolicy("AtLeast21", new AtLeast21Policy())]);

        var error = await authorizer.AuthorizeAsync(
            Requirements(policies: ["AtLeast21"]), new GuardedCommand(1), TestContext.Current.CancellationToken);

        error.Should().BeNull();
    }

    [Fact]
    public async Task GlobalRuleDeniesWithItsOwnErrorKind() {
        var user = new FakeCurrentUser { IsAuthenticated = true };
        var calls = new List<string>();
        var forbidding = Create(user,
            globalRules: [new RecordingGlobalRule(AppError.Forbidden("Workspace suspended."), calls, "forbid")]);

        var forbidden = await forbidding.AuthorizeAsync(
            Requirements(requireAuthenticated: true), null, TestContext.Current.CancellationToken);

        forbidden!.Kind.Should().Be(ErrorKind.Forbidden);
        forbidden.Message.Should().Be("Workspace suspended.");

        // A rule that must not disclose the resource answers NotFound; the authorizer passes it through
        // unchanged rather than reshaping every denial into Forbidden.
        var hiding = Create(user,
            globalRules: [new RecordingGlobalRule(AppError.NotFound("Not found."), calls, "hide")]);

        var notFound = await hiding.AuthorizeAsync(
            Requirements(requireAuthenticated: true), null, TestContext.Current.CancellationToken);

        notFound!.Kind.Should().Be(ErrorKind.NotFound);
        notFound.Message.Should().Be("Not found.");
    }

    [Fact]
    public async Task GlobalRulesRunInRegistrationOrderAndFirstDenialWins() {
        var user = new FakeCurrentUser { IsAuthenticated = true };
        var calls = new List<string>();
        var authorizer = Create(user, globalRules: [
            new RecordingGlobalRule(null, calls, "first"),
            new RecordingGlobalRule(AppError.Forbidden("second denied"), calls, "second"),
            new RecordingGlobalRule(AppError.Forbidden("third denied"), calls, "third")
        ]);

        var error = await authorizer.AuthorizeAsync(
            Requirements(requireAuthenticated: true), null, TestContext.Current.CancellationToken);

        error!.Message.Should().Be("second denied");
        calls.Should().Equal("first", "second");
    }

    [Fact]
    public async Task GlobalRuleRunsBeforeDeclaredRequirementsAndSeesTheRequest() {
        var user = new FakeCurrentUser { IsAuthenticated = true };
        var calls = new List<string>();
        var rule = new RecordingGlobalRule(AppError.Forbidden("rule denied"), calls, "rule");
        var resourceAuthorizer = new StubResourceAuthorizer();
        var authorizer = Create(user, resourceAuthorizer: resourceAuthorizer, globalRules: [rule]);

        var command = new GuardedCommand(7);
        var error = await authorizer.AuthorizeAsync(
            Requirements(
                permissions: ["tenants.read"],
                resources: [new ResourceRequirement(typeof(string), "Tenant", ResourceOperation.Read, 7)]),
            command,
            TestContext.Current.CancellationToken);

        // The rule's message (not the generic permission denial) proves it short-circuited first.
        error!.Message.Should().Be("rule denied");
        resourceAuthorizer.Calls.Should().BeEmpty();
        rule.LastContext!.Resource.Should().BeSameAs(command);
        rule.LastContext.User.Should().BeSameAs(user);
    }

    [Fact]
    public async Task GlobalRulesAreSkippedForAllowAnonymous() {
        var calls = new List<string>();
        var authorizer = Create(
            new FakeCurrentUser { IsAuthenticated = false },
            globalRules: [new RecordingGlobalRule(AppError.Forbidden("never"), calls, "rule")]);

        var error = await authorizer.AuthorizeAsync(
            Requirements(allowAnonymous: true, permissions: ["x"]), null, TestContext.Current.CancellationToken);

        error.Should().BeNull();
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UnauthenticatedYieldsUnauthorizedBeforeGlobalRulesRun() {
        var calls = new List<string>();
        var authorizer = Create(
            new FakeCurrentUser { IsAuthenticated = false },
            globalRules: [new RecordingGlobalRule(AppError.Forbidden("never"), calls, "rule")]);

        var error = await authorizer.AuthorizeAsync(
            Requirements(permissions: ["tenants.read"]), null, TestContext.Current.CancellationToken);

        error!.Kind.Should().Be(ErrorKind.Unauthorized);
        calls.Should().BeEmpty();
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
