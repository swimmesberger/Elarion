using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Authorization;

public sealed class AuthorizationPolicyRegistrationTests {
    [Fact]
    public void AddElarionAuthorizationRegistersOptionsAndAuthorizer() {
        var services = new ServiceCollection();
        services.AddSingleton<Elarion.Abstractions.Identity.ICurrentUser>(new FakeCurrentUser());
        services.AddLogging();
        services.AddElarionAuthorization(options => options.PermissionClaimType = "perm");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<AuthorizationOptions>().PermissionClaimType.Should().Be("perm");
        provider.GetRequiredService<IAuthorizer>().Should().BeOfType<ClaimsAuthorizer>();
    }

    [Fact]
    public void AddElarionAuthorizationResolvesTheInterfaceToTheSameScopedClaimsAuthorizer() {
        var services = new ServiceCollection();
        services.AddSingleton<Elarion.Abstractions.Identity.ICurrentUser>(new FakeCurrentUser());
        services.AddLogging();
        services.AddElarionAuthorization();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // The interface delegates to the concrete registration, so a decorator taking ClaimsAuthorizer as its
        // inner authorizer shares the scope's instance instead of constructing a second one.
        var concrete = scope.ServiceProvider.GetRequiredService<ClaimsAuthorizer>();
        scope.ServiceProvider.GetRequiredService<IAuthorizer>().Should().BeSameAs(concrete);
    }

    [Fact]
    public async Task HostRegisteredAuthorizerBeforeAddElarionAuthorizationWinsAndCanDecorateTheConcreteInner() {
        var services = new ServiceCollection();
        services.AddSingleton<Elarion.Abstractions.Identity.ICurrentUser>(
            new FakeCurrentUser { IsAuthenticated = true });
        services.AddLogging();
        // Registered *before* the framework call: every framework registration is TryAdd, so this wins.
        services.AddScoped<IAuthorizer, CountingAuthorizer>();
        services.AddElarionAuthorization();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var authorizer = scope.ServiceProvider.GetRequiredService<IAuthorizer>();
        authorizer.Should().BeOfType<CountingAuthorizer>();

        var error = await authorizer.AuthorizeAsync(
            new AuthorizationRequirements(false, false, ["tenants.read"], [], [], [], []),
            null,
            TestContext.Current.CancellationToken);

        // The decorator delegated to the shipped ClaimsAuthorizer, which denied the missing permission.
        error!.Kind.Should().Be(ErrorKind.Forbidden);
        ((CountingAuthorizer)authorizer).Calls.Should().Be(1);
    }

    [Fact]
    public void AddElarionGlobalAuthorizationRuleIsAdditiveAndDeduplicates() {
        var services = new ServiceCollection();
        services.AddElarionGlobalAuthorizationRule<PassingRule>();
        services.AddElarionGlobalAuthorizationRule<PassingRule>();
        services.AddElarionGlobalAuthorizationRule<DenyingRule>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<IGlobalAuthorizationRule>()
            .Select(rule => rule.GetType())
            .Should().Equal(typeof(PassingRule), typeof(DenyingRule));
    }

    [Fact]
    public async Task RegisteredGlobalRulesReachTheResolvedAuthorizer() {
        var services = new ServiceCollection();
        services.AddSingleton<Elarion.Abstractions.Identity.ICurrentUser>(
            new FakeCurrentUser { IsAuthenticated = true });
        services.AddLogging();
        services.AddElarionAuthorization();
        services.AddElarionGlobalAuthorizationRule<DenyingRule>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var error = await scope.ServiceProvider.GetRequiredService<IAuthorizer>().AuthorizeAsync(
            new AuthorizationRequirements(false, true, [], [], [], [], []),
            null,
            TestContext.Current.CancellationToken);

        error!.Kind.Should().Be(ErrorKind.Forbidden);
        error.Message.Should().Be("rule denied");
    }

    private sealed class PassingRule : IGlobalAuthorizationRule {
        public ValueTask<AppError?> EvaluateAsync(AuthorizationContext context, CancellationToken cancellationToken) {
            return ValueTask.FromResult<AppError?>(null);
        }
    }

    private sealed class DenyingRule : IGlobalAuthorizationRule {
        public ValueTask<AppError?> EvaluateAsync(AuthorizationContext context, CancellationToken cancellationToken) {
            return ValueTask.FromResult<AppError?>(AppError.Forbidden("rule denied"));
        }
    }

    /// <summary>A host decorator that injects the concrete shipped authorizer as its inner — the documented seam.</summary>
    private sealed class CountingAuthorizer(ClaimsAuthorizer inner) : IAuthorizer {
        public int Calls { get; private set; }

        public ValueTask<AppError?> AuthorizeAsync(
            AuthorizationRequirements requirements, object? resource, CancellationToken ct) {
            Calls++;
            return inner.AuthorizeAsync(requirements, resource, ct);
        }
    }

    [Fact]
    public void AddElarionAuthorizationPolicyOfTReadsNameFromAttribute() {
        var services = new ServiceCollection();
        services.AddElarionAuthorizationPolicy<AtLeast21Policy>();

        using var provider = services.BuildServiceProvider();

        var policies = provider.GetServices<NamedAuthorizationPolicy>().ToArray();
        policies.Should().ContainSingle(policy => policy.Name == "AtLeast21");
    }

    [Fact]
    public async Task DelegatePolicyIsRegisteredAndInvoked() {
        var services = new ServiceCollection();
        services.AddElarionAuthorizationPolicy(
            "EvenId",
            (context, _) => ValueTask.FromResult(context.Resource is GuardedCommand { Id: var id } && id % 2 == 0));

        using var provider = services.BuildServiceProvider();
        var named = provider.GetServices<NamedAuthorizationPolicy>().Single(candidate => candidate.Name == "EvenId");

        var even = await named.Policy.EvaluateAsync(
            new AuthorizationContext(new FakeCurrentUser(), new GuardedCommand(2)),
            TestContext.Current.CancellationToken);
        even.Should().BeTrue();
    }
}
