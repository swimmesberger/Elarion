using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;
using Elarion.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Authorization;

public sealed class AuthorizationDecoratorTests {
    private static AuthorizationDecorator<GuardedCommand, Result<string>> Decorate(
        Type handlerType,
        ICurrentUser user,
        bool requireAuthenticatedByDefault = false,
        IEnumerable<IAuthorizationPolicy>? policies = null,
        AuthorizationOptions? options = null) {
        var inner = new StubInnerHandler<GuardedCommand, Result<string>>(Result<string>.Success("ok"));
        var authorizer = new ClaimsAuthorizer(
            user, policies ?? [], options ?? new AuthorizationOptions(), NullLogger<ClaimsAuthorizer>.Instance);
        return new AuthorizationDecorator<GuardedCommand, Result<string>>(
            inner,
            new HandlerMetadata(handlerType, typeof(GuardedCommand), typeof(Result<string>)),
            authorizer,
            requireAuthenticatedByDefault);
    }

    [Fact]
    public async Task ForbidsWhenRequiredPermissionMissing() {
        var user = new FakeCurrentUser { IsAuthenticated = true };
        var decorator = Decorate(typeof(RequirePermissionHandler), user);

        var result = await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task AllowsWhenRequiredPermissionPresent() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("permission", "tenants.write")] };
        var decorator = Decorate(typeof(RequirePermissionHandler), user);

        var result = await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ok");
    }

    [Fact]
    public async Task UnauthenticatedYieldsUnauthorizedNotForbidden() {
        var user = new FakeCurrentUser { IsAuthenticated = false };
        var decorator = Decorate(typeof(RequirePermissionHandler), user);

        var result = await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }

    [Fact]
    public async Task MultiplePermissionsAreAnded() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("permission", "a")] };
        var decorator = Decorate(typeof(RequireBothPermissionsHandler), user);

        var result = await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task RequireClaimMatchesAnyAllowedValue() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("scope", "write")] };
        var decorator = Decorate(typeof(RequireClaimOrHandler), user);

        var result = await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AllowAnonymousWinsOverRequirePermission() {
        var user = new FakeCurrentUser { IsAuthenticated = false };
        var decorator = Decorate(typeof(AnonymousWinsHandler), user);

        var result = await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task PolicyAndRoleBothRequired() {
        var authorized = new FakeCurrentUser {
            IsAuthenticated = true, Roles = ["Admin"], Claims = [("age", "30")],
        };
        var policy = new AtLeast21Policy();
        var decorator = Decorate(typeof(PolicyAndRoleHandler), authorized, policies: [policy]);

        var allowed = await decorator.HandleAsync(new GuardedCommand(7), TestContext.Current.CancellationToken);

        allowed.IsSuccess.Should().BeTrue();
        // The policy receives the handler request as its resource.
        policy.LastResource.Should().BeOfType<GuardedCommand>().Which.Id.Should().Be(7);

        // Missing the role => forbidden even though the policy passes.
        var missingRole = new FakeCurrentUser { IsAuthenticated = true, Claims = [("age", "30")] };
        var deniedDecorator = Decorate(typeof(PolicyAndRoleHandler), missingRole, policies: [new AtLeast21Policy()]);
        var denied = await deniedDecorator.HandleAsync(new GuardedCommand(7), TestContext.Current.CancellationToken);
        denied.Error.Kind.Should().Be(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task SecureByDefaultRequiresAuthenticationForUnannotatedHandler() {
        var anonymous = new FakeCurrentUser { IsAuthenticated = false };
        var decorator = Decorate(typeof(NoAttributesHandler), anonymous, requireAuthenticatedByDefault: true);

        var result = await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);

        var authenticated = new FakeCurrentUser { IsAuthenticated = true };
        var allowedDecorator = Decorate(typeof(NoAttributesHandler), authenticated, requireAuthenticatedByDefault: true);
        var allowed = await allowedDecorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);
        allowed.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GuardFiresEvenWhenDecoratorIsOutermost() {
        // The decorator reads requirements from HandlerMetadata (the true handler type), not inner.GetType().
        // Here `inner` is an unrelated stub — simulating the decorator sitting OUTERMOST, where inner is the
        // next decorator. The guard must still fire (the fail-open footgun that HandlerMetadata exists to prevent).
        var user = new FakeCurrentUser { IsAuthenticated = true };
        var decorator = Decorate(typeof(RequirePermissionHandler), user);

        var result = await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.Forbidden);
    }
}
