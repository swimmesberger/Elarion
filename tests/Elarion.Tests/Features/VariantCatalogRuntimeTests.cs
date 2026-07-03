using AwesomeAssertions;
using Elarion.Abstractions.Features;
using Elarion.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Features;

public sealed class VariantCatalogRuntimeTests {
    [Fact]
    public void Catalog_FindByKey_IsCaseInsensitive_AndEmptyForUnknownKeys() {
        var descriptor = Descriptor("Email:Backend", ["smtp", "office365"]);
        var catalog = new DefaultVariantCatalog([descriptor]);

        catalog.All.Should().ContainSingle().Which.Should().BeSameAs(descriptor);
        catalog.FindByKey("email:backend").Should().ContainSingle().Which.Should().BeSameAs(descriptor);
        catalog.FindByKey("Payments:Gateway").Should().BeEmpty();
    }

    [Fact]
    public void AddElarionVariantCatalog_SeedsTheRuntimeCatalog() {
        var services = new ServiceCollection();
        services.AddElarionVariantCatalog([Descriptor("Email:Backend", ["smtp"])]);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IVariantCatalog>().FindByKey("Email:Backend").Should().HaveCount(1);
    }

    [Fact]
    public void NamedDefault_IsExplicitlySelectable_AndTheFallback() {
        // A named default ("linear") is registered under its own value key and doubles as the fallback, so an
        // admin can switch back to it by writing the value rather than by removing the key.
        using var absent = BuildServices(algorithm: null).BuildServiceProvider();
        using (var scope = absent.CreateScope()) {
            scope.ServiceProvider.GetRequiredService<IAlgorithm>().Should().BeOfType<LinearAlgorithm>();
        }

        using var explicitDefault = BuildServices("LINEAR").BuildServiceProvider();
        using (var scope = explicitDefault.CreateScope()) {
            scope.ServiceProvider.GetRequiredService<IAlgorithm>().Should().BeOfType<LinearAlgorithm>();
        }

        using var variant = BuildServices("neural").BuildServiceProvider();
        using (var scope = variant.CreateScope()) {
            scope.ServiceProvider.GetRequiredService<IAlgorithm>().Should().BeOfType<NeuralAlgorithm>();
        }
    }

    [Fact]
    public async Task Validator_Strict_FailsStartup_WhenConfiguredValueIsNotOffered() {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new DefaultVariantCatalog([Descriptor("Email:Backend", ["smtp", "office365"])]);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var validator = new VariantConfigurationValidator(
            catalog,
            BuildConfiguration(("Email:Backend", "sendgrid")),
            provider,
            new VariantValidationOptions { Strict = true },
            NullLogger<VariantConfigurationValidator>.Instance);

        var act = () => validator.StartAsync(ct);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Email:Backend*sendgrid*falls back to the default*");
    }

    [Fact]
    public async Task Validator_Strict_FailsStartup_WhenPlatformContractIsNotRegistered() {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new DefaultVariantCatalog(
            [Descriptor("Email:Backend", ["smtp"], contract: typeof(IAlgorithm), module: null)]);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var validator = new VariantConfigurationValidator(
            catalog,
            BuildConfiguration(),
            provider,
            new VariantValidationOptions { Strict = true },
            NullLogger<VariantConfigurationValidator>.Instance);

        var act = () => validator.StartAsync(ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*no DI registration*");
    }

    [Fact]
    public async Task Validator_PassesCleanly_WhenValueOfferedAndContractRegistered() {
        var ct = TestContext.Current.CancellationToken;
        var services = BuildServices("neural");
        using var provider = services.BuildServiceProvider();
        var catalog = new DefaultVariantCatalog(
            [Descriptor("Forecast:Algorithm", ["linear", "neural"], contract: typeof(IAlgorithm), module: null)]);
        using var validator = new VariantConfigurationValidator(
            catalog,
            provider.GetRequiredService<IConfiguration>(),
            provider,
            new VariantValidationOptions { Strict = true },
            NullLogger<VariantConfigurationValidator>.Instance);

        await validator.StartAsync(ct);
        await validator.StopAsync(ct);
    }

    [Fact]
    public async Task Validator_NonStrict_StartsDespiteFindings() {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new DefaultVariantCatalog([Descriptor("Email:Backend", ["smtp"])]);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var validator = new VariantConfigurationValidator(
            catalog,
            BuildConfiguration(("Email:Backend", "sendgrid")),
            provider,
            new VariantValidationOptions(),
            NullLogger<VariantConfigurationValidator>.Instance);

        await validator.StartAsync(ct);
        await validator.StopAsync(ct);
    }

    private static VariantDescriptor Descriptor(
        string key, string[] values, Type? contract = null, string? module = "App") => new() {
        Axis = VariantAxis.Configuration,
        Key = key,
        ContractName = contract?.FullName ?? key,
        Contract = contract,
        Values = values,
        DefaultValue = values.Length > 0 ? values[0] : null,
        HasDefault = true,
        Module = module,
    };

    private static ServiceCollection BuildServices(string? algorithm) {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(BuildConfiguration(
            algorithm is null ? [] : [("Forecast:Algorithm", algorithm)]));
        services.AddElarionConfigurationVariantService<IAlgorithm>("Forecast:Algorithm", defaultKey: "linear");
        services.AddKeyedScoped<IAlgorithm, LinearAlgorithm>("linear");
        services.AddKeyedScoped<IAlgorithm, NeuralAlgorithm>("neural");
        return services;
    }

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] values) {
        var data = new Dictionary<string, string?>();
        foreach (var (key, value) in values) {
            data[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private interface IAlgorithm;

    private sealed class LinearAlgorithm : IAlgorithm;

    private sealed class NeuralAlgorithm : IAlgorithm;
}
