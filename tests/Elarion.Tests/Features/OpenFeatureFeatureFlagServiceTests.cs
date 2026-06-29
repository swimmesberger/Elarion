using AwesomeAssertions;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using Elarion.FeatureManagement;
using Elarion.OpenFeature;
using Elarion.Tests.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OpenFeature.Providers.Memory;
using Xunit;

namespace Elarion.Tests.Features;

public sealed class OpenFeatureFeatureFlagServiceTests {
    private static Dictionary<string, Flag> Flags() => new() {
        ["feature-on"] = new Flag<bool>(new Dictionary<string, bool> { ["on"] = true, ["off"] = false }, "on"),
        ["feature-off"] = new Flag<bool>(new Dictionary<string, bool> { ["on"] = true, ["off"] = false }, "off"),
    };

    [Fact]
    public async Task EvaluatesBooleanFlagsAndFailsClosedOnUnknownFlag() {
        var ct = TestContext.Current.CancellationToken;
        await Api.Instance.SetProviderAsync(new InMemoryProvider(Flags()), ct);

        var service = new OpenFeatureFeatureFlagService(
            Api.Instance.GetClient(),
            new FakeCurrentUser { IsAuthenticated = true, UserId = "u-1" });

        (await service.IsEnabledAsync("feature-on", ct)).Should().BeTrue();
        (await service.IsEnabledAsync("feature-off", ct)).Should().BeFalse();
        // An unknown flag fails closed (default false).
        (await service.IsEnabledAsync("missing", ct)).Should().BeFalse();
    }

    [Fact]
    public void AddElarionFeatureManagement_RegistersFeatureFlagService() {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser());

        services.AddElarionFeatureManagement(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetService<IFeatureFlagService>()
            .Should().BeOfType<OpenFeatureFeatureFlagService>();
    }
}
