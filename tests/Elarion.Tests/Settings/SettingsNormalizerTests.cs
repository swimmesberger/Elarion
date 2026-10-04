using AwesomeAssertions;
using Elarion.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class SettingsNormalizerTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Normalize_RewritesLegacyRawStringsToTheirJsonForm_AndIsIdempotent() {
        using var provider = SettingsTestHost.Build();
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SetAsync(SettingsScope.Global, "app:title", "legacy raw value", null, cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, "app:plain", "42", null, cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, "user:theme", "\"already json\"", null, cancellationToken: Ct);
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var first = await normalizer.NormalizeAsync(SettingsScope.Global, cancellationToken: Ct);
        var second = await normalizer.NormalizeAsync(SettingsScope.Global, cancellationToken: Ct);

        first.Scanned.Should().Be(3);
        first.Normalized.Should().Be(2);
        first.UnreadableKeys.Should().BeEmpty();
        second.Normalized.Should().Be(0);
        (await store.GetAsync(SettingsScope.Global, "app:title", Ct))!.Value.Value.Should()
            .Be("\"legacy raw value\"");
        (await store.GetAsync(SettingsScope.Global, "app:plain", Ct))!.Value.Value.Should().Be("\"42\"");
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        (await manager.GetAsync(TestSettings.Title, cancellationToken: Ct)).Should().Be("legacy raw value");
    }

    [Fact]
    public async Task Normalize_NeverGuessesForNonStringTypes_AndReportsThemAsUnreadable() {
        using var provider = SettingsTestHost.Build();
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SetAsync(SettingsScope.Global, "app:smtp:port", "not-a-number", null, cancellationToken: Ct);
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var report = await normalizer.NormalizeAsync(SettingsScope.Global, cancellationToken: Ct);

        report.Normalized.Should().Be(0);
        report.UnreadableKeys.Should().Equal("app:smtp:port");
        (await store.GetAsync(SettingsScope.Global, "app:smtp:port", Ct))!.Value.Value.Should().Be("not-a-number");
    }

    [Fact]
    public async Task Normalize_ConvertsAPlaintextLegacySecret_IntoAProtectedJsonString() {
        var protector = new FakeSettingValueProtector();
        using var provider = SettingsTestHost.Build(protector: protector);
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SetAsync(SettingsScope.Global, "app:apikey", "raw-secret", null, cancellationToken: Ct);
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var report = await normalizer.NormalizeAsync(SettingsScope.Global, cancellationToken: Ct);

        report.Normalized.Should().Be(1);
        var entry = (await store.GetAsync(SettingsScope.Global, "app:apikey", Ct))!.Value;
        entry.Protection.Should().Be(protector.Scheme);
        entry.Value.Should().NotContain("raw-secret");
        (await SettingsTestHost.Scoped<ISettingsManager>(provider)
            .GetAsync(TestSettings.ApiKey, cancellationToken: Ct)).Should().Be("raw-secret");
    }

    [Fact]
    public async Task Normalize_HonoursTheKeyPrefix() {
        using var provider = SettingsTestHost.Build();
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SetAsync(SettingsScope.Global, "app:title", "legacy", null, cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, "app:plain", "legacy", null, cancellationToken: Ct);
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var report = await normalizer.NormalizeAsync(SettingsScope.Global, "app:title", Ct);

        report.Scanned.Should().Be(1);
        (await store.GetAsync(SettingsScope.Global, "app:plain", Ct))!.Value.Value.Should().Be("legacy");
    }
}
