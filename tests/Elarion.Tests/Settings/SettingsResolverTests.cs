using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Settings;
using Elarion.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class SettingsResolverTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Config(params (string Key, string? Value)[] values) {
        return values.ToDictionary(v => v.Key, v => v.Value);
    }

    [Fact]
    public async Task Layers_DefaultThenStoreThenConfigurationPin() {
        using var provider = SettingsTestHost.Build(Config(("app:title", "pinned")));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SetAsync(SettingsScope.Global, "app:smtp:port", "587", null, cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, "app:title", "\"stored\"", null, cancellationToken: Ct);

        (await manager.GetAsync(TestSettings.Enabled, cancellationToken: Ct)).Should().BeTrue();
        (await manager.GetAsync(TestSettings.Port, cancellationToken: Ct)).Should().Be(587);
        (await manager.GetAsync(TestSettings.Title, cancellationToken: Ct)).Should().Be("pinned");
    }

    [Fact]
    public async Task ManagerReadsThroughTheResolver_SoCodeSeesTheEffectiveValue() {
        using var provider = SettingsTestHost.Build(Config(("app:title", "pinned")));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var resolver = SettingsTestHost.Scoped<ISettingResolver>(provider);

        var resolved = await resolver.ResolveAsync(TestSettings.Title, SettingsScope.Global, Ct);

        resolved.Source.Should().Be(SettingSource.Configuration);
        resolved.IsPinned.Should().BeTrue();
        (await manager.GetAsync(TestSettings.Title, cancellationToken: Ct)).Should().Be("pinned");
    }

    [Fact]
    public async Task ConfigurationValue_IgnoredForNonPinnableDefinitions() {
        using var provider = SettingsTestHost.Build(Config(("app:plain", "from-config")));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        (await manager.GetAsync(TestSettings.Plain, cancellationToken: Ct)).Should().BeNull();
        var write = await manager.SetAsync(TestSettings.Plain, "runtime", cancellationToken: Ct);
        write.IsSuccess.Should().BeTrue();
        (await manager.GetAsync(TestSettings.Plain, cancellationToken: Ct)).Should().Be("runtime");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyConfigurationValue_DoesNotPin_ByDefault(string empty) {
        using var provider = SettingsTestHost.Build(Config(("app:title", empty)));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var resolver = SettingsTestHost.Scoped<ISettingResolver>(provider);
        await manager.SetAsync(TestSettings.Title, "stored", cancellationToken: Ct);

        var resolved = await resolver.ResolveAsync(TestSettings.Title, SettingsScope.Global, Ct);

        resolved.IsPinned.Should().BeFalse();
        resolved.Source.Should().Be(SettingSource.Store);
        (await manager.GetAsync(TestSettings.Title, cancellationToken: Ct)).Should().Be("stored");
    }

    [Fact]
    public async Task EmptyConfigurationValue_Pins_WhenConfigured() {
        using var provider = SettingsTestHost.Build(Config(("app:title", "")),
            configure: o => o.EmptyConfigurationValuesPin = true);
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SetAsync(SettingsScope.Global, "app:title", "\"stored\"", null, cancellationToken: Ct);

        (await manager.GetAsync(TestSettings.Title, cancellationToken: Ct)).Should().Be("");
        (await manager.SetAsync(TestSettings.Title, "x", cancellationToken: Ct)).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task WriteToPinnedDefinition_IsRefusedWithTypedFailure_AndStoreIsUntouched() {
        using var provider = SettingsTestHost.Build(Config(("app:title", "pinned")));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var store = provider.GetRequiredService<ISettingsStore>();

        var write = await manager.SetAsync(TestSettings.Title, "runtime", cancellationToken: Ct);
        var reset = await manager.ResetAsync(TestSettings.Title, cancellationToken: Ct);

        write.IsSuccess.Should().BeFalse();
        write.Error.Kind.Should().Be(ErrorKind.BusinessRule);
        write.Error.Data.Should().Be(new SettingWriteFailure("app:title", SettingWriteFailureReason.Pinned));
        reset.IsSuccess.Should().BeFalse();
        reset.Error.Data.Should().Be(new SettingWriteFailure("app:title", SettingWriteFailureReason.Pinned));
        (await store.GetAsync(SettingsScope.Global, "app:title", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task NonStringPinnedValues_AreParsedFromConfigurationText() {
        using var provider = SettingsTestHost.Build(Config(
            ("app:smtp:port", "2525"),
            ("app:enabled", "False"),
            ("app:widgets", """{"maxItems":9,"title":"cfg"}""")));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        (await manager.GetAsync(TestSettings.Port, cancellationToken: Ct)).Should().Be(2525);
        (await manager.GetAsync(TestSettings.Enabled, cancellationToken: Ct)).Should().BeFalse();
        (await manager.GetAsync(TestSettings.Widgets, cancellationToken: Ct)).Should()
            .Be(new WidgetSettings { MaxItems = 9, Title = "cfg" });
    }

    [Fact]
    public async Task InvalidPinnedValue_FailsLoudly() {
        using var provider = SettingsTestHost.Build(Config(("app:smtp:port", "not-a-number")));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        var act = async () => await manager.GetAsync(TestSettings.Port, cancellationToken: Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*app:smtp:port*");
    }

    [Fact]
    public async Task UserScope_IsNeverPinned() {
        using var provider = SettingsTestHost.Build(Config(("user:theme", "dark-pinned")));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        await manager.SetAsync(TestSettings.Theme, "dark", SettingsScope.User("u1"), cancellationToken: Ct);

        (await manager.GetAsync(TestSettings.Theme, SettingsScope.User("u1"), Ct)).Should().Be("dark");
    }

    [Fact]
    public async Task ProjectionProviders_AreNotConsultedForPins() {
        var projection = new TestProjectionProvider();
        projection.SetValue("app:title", "from-projection");
        var configuration = new ConfigurationManager();
        ((IConfigurationBuilder)configuration).Add(new TestProjectionSource(projection));
        using var provider = SettingsTestHost.Build(configurationRoot: configuration);
        var resolver = SettingsTestHost.Scoped<ISettingResolver>(provider);

        var resolved = await resolver.ResolveAsync(TestSettings.Title, SettingsScope.Global, Ct);

        resolved.IsPinned.Should().BeFalse();
        resolved.Source.Should().Be(SettingSource.Default);
    }

    [Fact]
    public async Task PinAppearingAfterAStoreWrite_WinsOverTheStoredValue() {
        var configuration = new ConfigurationManager();
        using var provider = SettingsTestHost.Build(configurationRoot: configuration);
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        await manager.SetAsync(TestSettings.Title, "stored", cancellationToken: Ct);
        (await manager.GetAsync(TestSettings.Title, cancellationToken: Ct)).Should().Be("stored");

        configuration["app:title"] = "pinned-later";

        (await manager.GetAsync(TestSettings.Title, cancellationToken: Ct)).Should().Be("pinned-later");
    }

    [Fact]
    public void DuplicateKeys_AreRejectedWhenTheCatalogIsBuilt() {
        var clash = new SettingDefinition<string>("APP:TITLE", new SettingDefinitionOptions());
        using var provider = SettingsTestHost.Build(configure: o => o.AddDefinition(clash));

        var act = () => provider.GetRequiredService<ISettingDefinitionCatalog>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*more than one definition*");
    }

    private sealed class TestProjectionSource(TestProjectionProvider provider) : IConfigurationSource {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }

    private sealed class TestProjectionProvider : ConfigurationProvider, ISettingsProjectionProvider {
        public void SetValue(string key, string value) => Data[key] = value;
    }
}
