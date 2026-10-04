using System.Text;
using AwesomeAssertions;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using Elarion.FeatureFlags.FeatureManagement;
using Elarion.FeatureFlags.OpenFeature;
using Elarion.Features;
using Elarion.Tests.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFeature;
using OpenFeature.Contrib.Providers.FeatureManagement;
using OpenFeature.Providers.Memory;
using Xunit;

namespace Elarion.Tests.Features;

// One class, because OpenFeature's provider registry (Api.Instance) is process-global: tests that swap the
// provider must not run in parallel with each other.
public sealed class OpenFeatureFlagEvaluatorTests {
    private static readonly IServiceProvider EmptyServices = new ServiceCollection().BuildServiceProvider();

    private static FeatureEvaluationContext ContextFor(string userId) {
        return new FeatureEvaluationContext { Services = EmptyServices, UserId = userId };
    }

    private static Dictionary<string, Flag> Flags() {
        return new Dictionary<string, Flag> {
            ["feature-on"] = new Flag<bool>(new Dictionary<string, bool> { ["on"] = true, ["off"] = false }, "on"),
            ["feature-off"] = new Flag<bool>(new Dictionary<string, bool> { ["on"] = true, ["off"] = false }, "off")
        };
    }

    [Fact]
    public async Task EvaluatesBooleanFlagsAndFailsClosedOnUnknownFlag() {
        var ct = TestContext.Current.CancellationToken;
        await Api.Instance.SetProviderAsync(new InMemoryProvider(Flags()), ct);

        var evaluator = new OpenFeatureFlagEvaluator(Api.Instance.GetClient());

        (await evaluator.IsEnabledAsync("feature-on", ContextFor("u-1"), ct)).Should().BeTrue();
        (await evaluator.IsEnabledAsync("feature-off", ContextFor("u-1"), ct)).Should().BeFalse();
        // An unknown flag fails closed (default false).
        (await evaluator.IsEnabledAsync("missing", ContextFor("u-1"), ct)).Should().BeFalse();
    }

    [Fact]
    public void AddElarionFeatureManagement_RegistersTheBackendEvaluator() {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser());

        services.AddElarionFeatureManagement(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetService<IBackendFeatureFlagEvaluator>()
            .Should().BeOfType<OpenFeatureFlagEvaluator>();
    }

    [Fact]
    public async Task Evaluator_ReadsAllocatedVariantName_AndNullForUnknownFlag() {
        var ct = TestContext.Current.CancellationToken;
        var flags = new Dictionary<string, Flag> {
            ["algo"] = new Flag<string>(new Dictionary<string, string> { ["neural"] = "n", ["linear"] = "l" }, "neural")
        };
        await Api.Instance.SetProviderAsync(new InMemoryProvider(flags), ct);

        var evaluator = new OpenFeatureFlagEvaluator(Api.Instance.GetClient());

        (await evaluator.GetVariantAsync("algo", ContextFor("u-1"), ct)).Should().Be("neural");
        (await evaluator.GetVariantAsync("missing", ContextFor("u-1"), ct)).Should().BeNull();
    }

    [Fact]
    public async Task BackendDeclaredFlag_IsOwnedByTheOpenFeatureEvaluator_ThroughTheCatalogService() {
        var ct = TestContext.Current.CancellationToken;
        await Api.Instance.SetProviderAsync(new InMemoryProvider(Flags()), ct);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionBackendFeatureFlag("feature-on", "Billing");
        services.AddElarionBackendFeatureFlag("feature-off", "Billing");
        services.AddSingleton<IFeatureClient>(Api.Instance.GetClient());
        services.AddElarionOpenFeature();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var flags = scope.ServiceProvider.GetRequiredService<IFeatureFlagService>();

        (await flags.IsEnabledAsync("feature-on", ct)).Should().BeTrue();
        (await flags.IsEnabledAsync("feature-off", ct)).Should().BeFalse();
        // Not declared: the backend is never consulted as a fallback, even though it would answer "missing".
        (await flags.IsEnabledAsync("missing", ct)).Should().BeFalse();
    }

    [Fact]
    public async Task BackendDeclaredFlag_IsOwnedByTheMicrosoftFeatureManagementProvider() {
        var ct = TestContext.Current.CancellationToken;
        const string json =
            """
            { "feature_management": { "feature_flags": [
                { "id": "managed", "enabled": true },
                { "id": "dark", "enabled": false } ] } }
            """;
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        await Api.Instance.SetProviderAsync(new FeatureManagementProvider(configuration), ct);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionBackendFeatureFlag("managed", "Billing");
        services.AddElarionBackendFeatureFlag("dark", "Billing");
        services.AddSingleton<IFeatureClient>(Api.Instance.GetClient());
        services.AddElarionOpenFeature();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var flags = scope.ServiceProvider.GetRequiredService<IFeatureFlagService>();

        (await flags.IsEnabledAsync("managed", ct)).Should().BeTrue();
        (await flags.IsEnabledAsync("dark", ct)).Should().BeFalse();
    }

    [Fact]
    public async Task ExplicitContext_TargetsTheSuppliedUser_NotTheAmbientOne() {
        var ct = TestContext.Current.CancellationToken;
        var flags = new Dictionary<string, Flag> {
            ["rollout"] = new Flag<bool>(
                new Dictionary<string, bool> { ["on"] = true, ["off"] = false }, "off",
                // Targeting rule: only user u-vip gets the "on" variant.
                context => context.TargetingKey == "u-vip" ? "on" : "off")
        };
        await Api.Instance.SetProviderAsync(new InMemoryProvider(flags), ct);
        var evaluator = new OpenFeatureFlagEvaluator(Api.Instance.GetClient());

        (await evaluator.IsEnabledAsync("rollout", ContextFor("u-vip"), ct)).Should().BeTrue();
        (await evaluator.IsEnabledAsync("rollout", ContextFor("u-other"), ct)).Should().BeFalse();
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
        details.Value.Should().Be("n"); // the variant IS evaluated (neural's configuration_value)
        details.Variant.Should().BeNullOrEmpty(); // but the variant NAME is not surfaced — the preview limitation

        var evaluator = new OpenFeatureFlagEvaluator(Api.Instance.GetClient());

        // Consequently, variant-service injection is unavailable through the Microsoft.FeatureManagement provider.
        (await evaluator.GetVariantAsync("algo", ContextFor("u-1"), ct)).Should().BeNull();
    }
}
