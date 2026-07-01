using AwesomeAssertions;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using Elarion.FeatureFlags.FeatureManagement;
using Elarion.FeatureFlags.OpenFeature;
using Elarion.Tests.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using OpenFeature;
using OpenFeature.Contrib.Providers.FeatureManagement;
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

    [Fact]
    public async Task VariantService_ReadsAllocatedVariantName_AndNullForUnknownFlag() {
        var ct = TestContext.Current.CancellationToken;
        var flags = new Dictionary<string, Flag> {
            ["algo"] = new Flag<string>(new Dictionary<string, string> { ["neural"] = "n", ["linear"] = "l" }, "neural"),
        };
        await Api.Instance.SetProviderAsync(new InMemoryProvider(flags), ct);

        var service = new OpenFeatureFeatureVariantService(
            Api.Instance.GetClient(), new FakeCurrentUser { IsAuthenticated = true, UserId = "u-1" });

        (await service.GetVariantAsync("algo", ct)).Should().Be("neural");
        (await service.GetVariantAsync("missing", ct)).Should().BeNull();
    }

    // The gate (ADR-0019 risk #2): the preview OpenFeature.Contrib.Provider.FeatureManagement (0.1.2-preview)
    // EVALUATES the variant (returning its configuration_value as .Value) but does NOT populate
    // FlagEvaluationDetails.Variant with the variant NAME. So variant *service injection* requires a native
    // OpenFeature provider (InMemory/flagd/LaunchDarkly/ConfigCat) that surfaces .Variant per spec §1.4.6. This
    // guards that documented behavior so a future provider upgrade that fixes it surfaces here.
    [Fact]
    public async Task MicrosoftFeatureManagementProvider_DoesNotYetSurfaceVariantName() {
        var ct = TestContext.Current.CancellationToken;
        const string json =
            """
            {
              "feature_management": {
                "feature_flags": [
                  {
                    "id": "algo",
                    "enabled": true,
                    "variants": [
                      { "name": "neural", "configuration_value": "n" },
                      { "name": "linear", "configuration_value": "l" }
                    ],
                    "allocation": { "default_when_enabled": "neural" }
                  }
                ]
              }
            }
            """;
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        await Api.Instance.SetProviderAsync(new FeatureManagementProvider(configuration), ct);

        var details = await Api.Instance.GetClient().GetStringDetailsAsync("algo", "DEFAULT", cancellationToken: ct);
        details.Value.Should().Be("n");            // the variant IS evaluated (neural's configuration_value)
        details.Variant.Should().BeNullOrEmpty();  // but the variant NAME is not surfaced — the preview limitation

        var service = new OpenFeatureFeatureVariantService(
            Api.Instance.GetClient(), new FakeCurrentUser { IsAuthenticated = true, UserId = "u-1" });

        // Consequently, variant-service injection is unavailable through the Microsoft.FeatureManagement provider.
        (await service.GetVariantAsync("algo", ct)).Should().BeNull();
    }
}
