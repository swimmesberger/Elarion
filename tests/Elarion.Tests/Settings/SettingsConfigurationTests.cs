using AwesomeAssertions;
using Elarion.Abstractions.Serialization;
using Elarion.Abstractions.Settings;
using Elarion.Settings;
using Elarion.Settings.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class SettingsConfigurationTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Data(params (string Key, string? Value)[] values) {
        return values.ToDictionary(v => v.Key, v => v.Value);
    }

    [Fact]
    public void Source_Build_ReturnsTheSharedProvider() {
        var source = new SettingsConfigurationSource();

        source.Build(new ConfigurationBuilder()).Should().BeSameAs(source.Provider);
    }

    [Fact]
    public void Provider_IsMarkedAsAProjection() {
        new SettingsConfigurationProvider().Should().BeAssignableTo<ISettingsProjectionProvider>();
    }

    [Fact]
    public void Apply_ExposesValuesAndHierarchyThroughConfiguration() {
        var source = new SettingsConfigurationSource();
        var configuration = new ConfigurationBuilder().Add(source).Build();

        source.Provider.Apply(Data(("app:title", "Elarion"), ("app:smtp:port", "25")));

        configuration["app:title"].Should().Be("Elarion");
        configuration.GetSection("app:smtp")["port"].Should().Be("25");
    }

    [Fact]
    public void Apply_FiresConfigurationReloadToken_OnlyWhenDataChanged() {
        var source = new SettingsConfigurationSource();
        var configuration = new ConfigurationBuilder().Add(source).Build();
        source.Provider.Apply(Data(("app:title", "Elarion")));

        var unchanged = configuration.GetReloadToken();
        source.Provider.Apply(Data(("app:title", "Elarion")));
        unchanged.HasChanged.Should().BeFalse();

        source.Provider.Apply(Data(("app:title", "Other")));
        unchanged.HasChanged.Should().BeTrue();
        configuration["app:title"].Should().Be("Other");
    }

    [Fact]
    public async Task Projection_ContainsEffectiveStoreAndDefaultValues_FlattenedForOptionsBinding() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        await manager.SetAsync(TestSettings.Port, 587, cancellationToken: Ct);
        await manager.SetAsync(TestSettings.Widgets, new WidgetSettings { MaxItems = 5, Title = "w" },
            cancellationToken: Ct);

        var data = await ProjectAsync(provider);

        data["app:smtp:port"].Should().Be("587");
        data["app:title"].Should().Be("Untitled");
        data["app:enabled"].Should().Be("true");
        data["app:widgets:maxItems"].Should().Be("5");
        data["app:widgets:title"].Should().Be("w");
        data.Should().NotContainKey("app:plain");
    }

    [Fact]
    public async Task Projection_NeverContainsSecrets() {
        using var provider = SettingsTestHost.Build(protector: new FakeSettingValueProtector());
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        await manager.SetAsync(TestSettings.ApiKey, "super-secret", cancellationToken: Ct);

        var data = await ProjectAsync(provider);

        data.Keys.Should().NotContain("app:apikey");
        data.Values.Should().NotContain(v => v != null && v.Contains("super-secret"));
    }

    [Fact]
    public async Task Projection_SkipsValuesPinnedByConfiguration() {
        using var provider = SettingsTestHost.Build(Data(("app:title", "pinned")));

        var data = await ProjectAsync(provider);

        data.Should().NotContainKey("app:title");
    }

    [Fact]
    public async Task Projection_ExcludesNonGlobalScopes() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        await manager.SetAsync(TestSettings.Theme, "dark", SettingsScope.User("u1"), cancellationToken: Ct);

        var data = await ProjectAsync(provider);

        data["user:theme"].Should().Be("light");
    }

    [Fact]
    public async Task Refresher_LoadsTheProjectionAndFollowsStoreChanges() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var configurationProvider = new SettingsConfigurationProvider();
        var refresher = CreateRefresher(provider, configurationProvider, new ConfigurationBuilder().Build());

        await manager.SetAsync(TestSettings.Title, "first", cancellationToken: Ct);
        await refresher.RefreshAsync(Ct);
        configurationProvider.TryGet("app:title", out var first).Should().BeTrue();
        first.Should().Be("first");

        await manager.SetAsync(TestSettings.Title, "second", cancellationToken: Ct);
        await refresher.RefreshAsync(Ct);
        configurationProvider.TryGet("app:title", out var second).Should().BeTrue();
        second.Should().Be("second");
    }

    [Fact]
    public async Task Refresher_StartAsync_LoadsBeforeReturning_AndPropagatesStoreChangesInBackground() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        await manager.SetAsync(TestSettings.Title, "Elarion", cancellationToken: Ct);
        var configurationProvider = new SettingsConfigurationProvider();
        var refresher = CreateRefresher(provider, configurationProvider, new ConfigurationBuilder().Build());

        await refresher.StartAsync(Ct);

        configurationProvider.TryGet("app:title", out var value).Should().BeTrue();
        value.Should().Be("Elarion");

        var reloaded = new TaskCompletionSource();
        using var subscription = Microsoft.Extensions.Primitives.ChangeToken.OnChange(
            configurationProvider.GetReloadToken, () => reloaded.TrySetResult());
        await manager.SetAsync(TestSettings.Title, "Changed", cancellationToken: Ct);
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        configurationProvider.TryGet("app:title", out var changed).Should().BeTrue();
        changed.Should().Be("Changed");
        await refresher.StopAsync(Ct);
    }

    [Fact]
    public async Task Refresher_ReprojectsWhenAPinAppearsInTheDeploymentConfiguration() {
        var projection = new SettingsConfigurationSource();
        var root = new ConfigurationManager();
        ((IConfigurationBuilder)root).Add(projection);
        using var provider = SettingsTestHost.Build(configurationRoot: root);
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        await manager.SetAsync(TestSettings.Title, "stored", cancellationToken: Ct);

        var refresher = CreateRefresher(provider, projection.Provider, root);
        await refresher.StartAsync(Ct);
        root["app:title"].Should().Be("stored");

        var reprojected = new TaskCompletionSource();
        using var subscription = Microsoft.Extensions.Primitives.ChangeToken.OnChange(
            projection.Provider.GetReloadToken, () => reprojected.TrySetResult());
        ((IConfigurationBuilder)root).AddInMemoryCollection(Data(("app:title", "pinned")));
        await reprojected.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // The pin now sits in IConfiguration and is no longer duplicated by the projection.
        projection.Provider.TryGet("app:title", out _).Should().BeFalse();
        await refresher.StopAsync(Ct);
    }

    private static async Task<IReadOnlyDictionary<string, string?>> ProjectAsync(ServiceProvider provider) {
        var resolver = SettingsTestHost.Scoped<ISettingResolver>(provider);
        var resolved = await resolver.ResolveAllAsync(SettingsScope.Global, null, Ct);
        return SettingsConfigurationProjection.Project(resolved,
            provider.GetRequiredService<IElarionJsonSerialization>());
    }

    private static SettingsConfigurationRefresher CreateRefresher(
        ServiceProvider provider,
        SettingsConfigurationProvider configurationProvider,
        IConfiguration configuration) {
        return new SettingsConfigurationRefresher(configurationProvider,
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ISettingsChangeSource>(),
            configuration,
            NullLogger<SettingsConfigurationRefresher>.Instance);
    }
}
