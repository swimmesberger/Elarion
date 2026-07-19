using AwesomeAssertions;
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
