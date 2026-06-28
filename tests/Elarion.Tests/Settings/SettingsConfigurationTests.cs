using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class SettingsConfigurationTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SettingEntry Entry(string key, string? value) => new(key, value, default, 1);

    [Fact]
    public void Source_Build_ReturnsTheSharedProvider() {
        var source = new SettingsConfigurationSource();

        source.Build(new ConfigurationBuilder()).Should().BeSameAs(source.Provider);
    }

    [Fact]
    public void Apply_ExposesValuesAndHierarchyThroughConfiguration() {
        var source = new SettingsConfigurationSource();
        var configuration = new ConfigurationBuilder().Add(source).Build();

        source.Provider.Apply([Entry("app:title", "Elarion"), Entry("app:smtp:port", "25")]);

        configuration["app:title"].Should().Be("Elarion");
        configuration.GetSection("app:smtp")["port"].Should().Be("25");
    }

    [Fact]
    public void Apply_FiresConfigurationReloadToken() {
        var source = new SettingsConfigurationSource();
        var configuration = new ConfigurationBuilder().Add(source).Build();
        var token = configuration.GetReloadToken();

        source.Provider.Apply([Entry("app:title", "Elarion")]);

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void Apply_ReflectsLatestValueOnReapply() {
        var source = new SettingsConfigurationSource();
        var configuration = new ConfigurationBuilder().Add(source).Build();

        source.Provider.Apply([Entry("app:title", "first")]);
        source.Provider.Apply([Entry("app:title", "second")]);

        configuration["app:title"].Should().Be("second");
    }

    [Fact]
    public async Task Refresher_LoadsGlobalSettingsIntoProvider() {
        using var provider = BuildSettingsProvider();
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SetAsync(SettingsScope.Global, "app:title", "Elarion", cancellationToken: Ct);
        var configurationProvider = new SettingsConfigurationProvider();
        var refresher = CreateRefresher(provider, configurationProvider);

        await refresher.RefreshAsync(Ct);

        configurationProvider.TryGet("app:title", out var value).Should().BeTrue();
        value.Should().Be("Elarion");
    }

    [Fact]
    public async Task Refresher_ExcludesNonGlobalScopes() {
        using var provider = BuildSettingsProvider();
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SetAsync(SettingsScope.User("u1"), "app:title", "user-only", cancellationToken: Ct);
        var configurationProvider = new SettingsConfigurationProvider();
        var refresher = CreateRefresher(provider, configurationProvider);

        await refresher.RefreshAsync(Ct);

        configurationProvider.TryGet("app:title", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Refresher_ReflectsUpdatedValueOnReload() {
        using var provider = BuildSettingsProvider();
        var store = provider.GetRequiredService<ISettingsStore>();
        var configurationProvider = new SettingsConfigurationProvider();
        var refresher = CreateRefresher(provider, configurationProvider);

        await store.SetAsync(SettingsScope.Global, "app:title", "first", cancellationToken: Ct);
        await refresher.RefreshAsync(Ct);
        await store.SetAsync(SettingsScope.Global, "app:title", "second", cancellationToken: Ct);
        await refresher.RefreshAsync(Ct);

        configurationProvider.TryGet("app:title", out var value).Should().BeTrue();
        value.Should().Be("second");
    }

    private static ServiceProvider BuildSettingsProvider() {
        var services = new ServiceCollection();
        services.AddElarionSettings();
        return services.BuildServiceProvider();
    }

    private static SettingsConfigurationRefresher CreateRefresher(
        ServiceProvider provider,
        SettingsConfigurationProvider configurationProvider) =>
        new(configurationProvider,
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ISettingsChangeSource>(),
            NullLogger<SettingsConfigurationRefresher>.Instance);
}
