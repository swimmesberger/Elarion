using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Diagnostics;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;
using Elarion.Authorization;
using Elarion.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Elarion.Pipeline;
using Elarion.Diagnostics;

namespace Elarion.Tests.Authorization;

public sealed class AuthorizationDecoratorTests {
    private static AuthorizationDecorator<GuardedCommand, Result<string>> Decorate(
        Type handlerType,
        ICurrentUser user,
        bool requireAuthenticatedByDefault = false,
        IEnumerable<NamedAuthorizationPolicy>? policies = null,
        AuthorizationOptions? options = null) {
        var inner = new StubInnerHandler<GuardedCommand, Result<string>>(Result<string>.Success("ok"));
        var authorizer = new ClaimsAuthorizer(
            user, policies ?? [], new StubResourceAuthorizer(), options ?? new AuthorizationOptions(),
            NullLogger<ClaimsAuthorizer>.Instance);
        return new AuthorizationDecorator<GuardedCommand, Result<string>>(
            inner,
            new HandlerMetadata(handlerType, typeof(GuardedCommand), typeof(Result<string>)),
            authorizer,
            requireAuthenticatedByDefault);
    }

    private static AuthorizationDecorator<GuardedCommand, Result<string>> DecorateWithResource(
        ICurrentUser user,
        IResourceAuthorizer resourceAuthorizer) {
        var inner = new StubInnerHandler<GuardedCommand, Result<string>>(Result<string>.Success("ok"));
        var authorizer = new ClaimsAuthorizer(
            user, [], resourceAuthorizer, new AuthorizationOptions(), NullLogger<ClaimsAuthorizer>.Instance);
        return new AuthorizationDecorator<GuardedCommand, Result<string>>(
            inner,
            new HandlerMetadata(typeof(NoAttributesHandler), typeof(GuardedCommand), typeof(Result<string>)),
            authorizer,
            false,
            [
                new ResourceRequirementBinding<GuardedCommand>(
                    typeof(string), ResourceOperation.Update, static command => command.Id)
            ]);
    }

    [Fact]
    public async Task RequireResource_ResolvesIdFromRequest_AndDeniesWhenAuthorizerDenies() {
        var resourceAuthorizer = new StubResourceAuthorizer(false);
        var decorator = DecorateWithResource(new FakeCurrentUser { IsAuthenticated = true }, resourceAuthorizer);

        var result = await decorator.HandleAsync(new GuardedCommand(42), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Forbidden);
        resourceAuthorizer.Calls.Should().ContainSingle();
        resourceAuthorizer.Calls[0].ResourceId.Should().Be(42);
        resourceAuthorizer.Calls[0].Operation.Should().Be(ResourceOperation.Update);
        resourceAuthorizer.Calls[0].ResourceType.Should().Be(typeof(string));
    }

    [Fact]
    public async Task RequireResource_AllowsWhenAuthorizerAllows() {
        var decorator = DecorateWithResource(
            new FakeCurrentUser { IsAuthenticated = true }, new StubResourceAuthorizer(true));

        var result = await decorator.HandleAsync(new GuardedCommand(7), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RequireResource_UnauthenticatedReturnsUnauthorized_WithoutCallingResourceAuthorizer() {
        var resourceAuthorizer = new StubResourceAuthorizer(true);
        var decorator = DecorateWithResource(new FakeCurrentUser { IsAuthenticated = false }, resourceAuthorizer);

        var result = await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
        resourceAuthorizer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RequireResource_PassesFullNameDiscriminatorByDefault() {
        var resourceAuthorizer = new StubResourceAuthorizer(true);
        var inner = new StubInnerHandler<GuardedCommand, Result<string>>(Result<string>.Success("ok"));
        var authorizer = new ClaimsAuthorizer(
            new FakeCurrentUser { IsAuthenticated = true }, [], resourceAuthorizer,
            new AuthorizationOptions(), NullLogger<ClaimsAuthorizer>.Instance);
        var decorator = new AuthorizationDecorator<GuardedCommand, Result<string>>(
            inner,
            new HandlerMetadata(typeof(NoAttributesHandler), typeof(GuardedCommand), typeof(Result<string>)),
            authorizer,
            false,
            [
                new ResourceRequirementBinding<GuardedCommand>(
                    typeof(Contact), ResourceOperation.Read, static command => command.Id)
            ]);

        await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        resourceAuthorizer.Calls[0].ResourceTypeName.Should().Be(typeof(Contact).FullName);
    }

    [Fact]
    public async Task RequireResource_PassesExplicitDiscriminatorOverride() {
        var resourceAuthorizer = new StubResourceAuthorizer(true);
        var inner = new StubInnerHandler<GuardedCommand, Result<string>>(Result<string>.Success("ok"));
        var authorizer = new ClaimsAuthorizer(
            new FakeCurrentUser { IsAuthenticated = true }, [], resourceAuthorizer,
            new AuthorizationOptions(), NullLogger<ClaimsAuthorizer>.Instance);
        var decorator = new AuthorizationDecorator<GuardedCommand, Result<string>>(
            inner,
            new HandlerMetadata(typeof(NoAttributesHandler), typeof(GuardedCommand), typeof(Result<string>)),
            authorizer,
            false,
            [
                new ResourceRequirementBinding<GuardedCommand>(
                    typeof(Contact), ResourceOperation.Read, static command => command.Id, "Contact")
            ]);

        await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        resourceAuthorizer.Calls[0].ResourceTypeName.Should().Be("Contact");
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
    public async Task Denial_TagsHandlerSpanAndRecordsDeniedMetric() {
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        using var handlerActivity = new Activity("handle").Start();
        var user = new FakeCurrentUser { IsAuthenticated = true };
        var decorator = Decorate(typeof(RequirePermissionHandler), user);

        var result = await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.Forbidden);
        handlerActivity.GetTagItem("elarion.authorization.outcome").Should().Be("forbidden");
        meters.Measurements.Should().Contain(m =>
            m.InstrumentName == "handler.authorization.denied.count" &&
            m.HasTag("elarion.handler", nameof(RequirePermissionHandler)) &&
            m.HasTag("elarion.authorization.outcome", "forbidden"));
    }

    [Fact]
    public async Task UnauthenticatedDenial_RecordsUnauthorizedOutcome() {
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        var decorator = Decorate(typeof(RequirePermissionHandler), new FakeCurrentUser { IsAuthenticated = false });

        await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        meters.Measurements.Should().Contain(m =>
            m.InstrumentName == "handler.authorization.denied.count" &&
            m.HasTag("elarion.handler", nameof(RequirePermissionHandler)) &&
            m.HasTag("elarion.authorization.outcome", "unauthorized"));
    }

    [Fact]
    public async Task MultiplePermissionsAreAnded() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("permission", "a.read")] };
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
            IsAuthenticated = true, Roles = ["Admin"], Claims = [("age", "30")]
        };
        var policy = new AtLeast21Policy();
        var decorator = Decorate(
            typeof(PolicyAndRoleHandler), authorized, policies: [new NamedAuthorizationPolicy("AtLeast21", policy)]);

        var allowed = await decorator.HandleAsync(new GuardedCommand(7), TestContext.Current.CancellationToken);

        allowed.IsSuccess.Should().BeTrue();
        // The policy receives the handler request as its resource.
        policy.LastResource.Should().BeOfType<GuardedCommand>().Which.Id.Should().Be(7);

        // Missing the role => forbidden even though the policy passes.
        var missingRole = new FakeCurrentUser { IsAuthenticated = true, Claims = [("age", "30")] };
        var deniedDecorator = Decorate(
            typeof(PolicyAndRoleHandler), missingRole,
            policies: [new NamedAuthorizationPolicy("AtLeast21", new AtLeast21Policy())]);
        var denied = await deniedDecorator.HandleAsync(new GuardedCommand(7), TestContext.Current.CancellationToken);
        denied.Error.Kind.Should().Be(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task SecureByDefaultRequiresAuthenticationForUnannotatedHandler() {
        var anonymous = new FakeCurrentUser { IsAuthenticated = false };
        var decorator = Decorate(typeof(NoAttributesHandler), anonymous, true);

        var result = await decorator.HandleAsync(new GuardedCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);

        var authenticated = new FakeCurrentUser { IsAuthenticated = true };
        var allowedDecorator = Decorate(typeof(NoAttributesHandler), authenticated, true);
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
