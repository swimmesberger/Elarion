using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions.Serialization;
using Elarion.Abstractions.Settings;
using Elarion.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class SettingsNormalizerTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SettingDefinition<long> Big = new("norm:big", new SettingDefinitionOptions());
    private static readonly SettingDefinition<decimal> Rate = new("norm:rate", new SettingDefinitionOptions());
    private static readonly SettingDefinition<NormMode> Mode = new("norm:mode", new SettingDefinitionOptions());
    private static readonly SettingDefinition<Guid> Id = new("norm:id", new SettingDefinitionOptions());
    private static readonly SettingDefinition<int?> MaybePort = new("norm:maybe", new SettingDefinitionOptions());
    private static readonly SettingDefinition<bool> SecretFlag = new("norm:secretflag",
        new SettingDefinitionOptions { IsSecret = true });

    private static ServiceProvider BuildHost(ISettingValueProtector? protector = null) {
        return SettingsTestHost.Build(
            protector: protector,
            configure: o => o.AddDefinitions([Big, Rate, Mode, Id, MaybePort, SecretFlag]),
            customize: services => services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(NormJsonContext.Default)));
    }

    private static Task SetAsync(ServiceProvider provider, string key, string value, string? protection = null) {
        return provider.GetRequiredService<ISettingsStore>()
            .SetAsync(SettingsScope.Global, key, value, protection, cancellationToken: Ct).AsTask();
    }

    private static async Task<string?> ValueAsync(ServiceProvider provider, string key) {
        return (await provider.GetRequiredService<ISettingsStore>().GetAsync(SettingsScope.Global, key, Ct))!.Value
            .Value;
    }

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
        first.Rewritten.Should().Be(2);
        first.AlreadyCanonical.Should().Be(1);
        first.Unreadable.Should().Be(0);
        second.Rewritten.Should().Be(0);
        second.AlreadyCanonical.Should().Be(3);
        (await ValueAsync(provider, "app:title")).Should().Be("\"legacy raw value\"");
        (await ValueAsync(provider, "app:plain")).Should().Be("\"42\"");
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        (await manager.GetAsync(TestSettings.Title, cancellationToken: Ct)).Should().Be("legacy raw value");
    }

    [Fact]
    public async Task Normalize_CoercesLegacyRawTextForScalarTypes() {
        using var provider = BuildHost();
        var id = Guid.CreateVersion7();
        await SetAsync(provider, "app:enabled", "True");
        await SetAsync(provider, "app:smtp:port", "030");
        await SetAsync(provider, "norm:big", "+9000000000");
        await SetAsync(provider, "norm:rate", " 1.50 ");
        await SetAsync(provider, "norm:mode", "fast");
        await SetAsync(provider, "norm:id", id.ToString());
        await SetAsync(provider, "norm:maybe", "7");
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var report = await normalizer.NormalizeAsync(SettingsScope.Global, cancellationToken: Ct);

        report.Rewritten.Should().Be(5);
        report.AlreadyCanonical.Should().Be(2);
        (await ValueAsync(provider, "app:enabled")).Should().Be("true");
        (await ValueAsync(provider, "app:smtp:port")).Should().Be("30");
        (await ValueAsync(provider, "norm:big")).Should().Be("9000000000");
        (await ValueAsync(provider, "norm:rate")).Should().Be(" 1.50 ");
        (await ValueAsync(provider, "norm:mode")).Should().Be("\"Fast\"");
        (await ValueAsync(provider, "norm:id")).Should().Be($"\"{id}\"");
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        (await manager.GetAsync(TestSettings.Port, cancellationToken: Ct)).Should().Be(30);
        (await manager.GetAsync(Mode, cancellationToken: Ct)).Should().Be(NormMode.Fast);
    }

    [Fact]
    public async Task Normalize_WhitespaceAroundValidJson_IsAlreadyCanonical() {
        using var provider = BuildHost();
        await SetAsync(provider, "app:smtp:port", " 30 ");
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var report = await normalizer.NormalizeAsync(SettingsScope.Global, cancellationToken: Ct);

        report.AlreadyCanonical.Should().Be(1);
        (await ValueAsync(provider, "app:smtp:port")).Should().Be(" 30 ");
    }

    [Fact]
    public async Task Normalize_ReportsUncoercibleValuesPerKey_WithoutTheValue() {
        using var provider = BuildHost();
        await SetAsync(provider, "app:smtp:port", "secret-looking-text");
        await SetAsync(provider, "app:widgets", "not json");
        await SetAsync(provider, "norm:mode", "sideways");
        await SetAsync(provider, "app:enabled", "yes");
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var report = await normalizer.NormalizeAsync(SettingsScope.Global, cancellationToken: Ct);

        report.Rewritten.Should().Be(0);
        report.Where(SettingNormalizationOutcome.Unreadable).Select(e => e.Key).Should()
            .BeEquivalentTo("app:smtp:port", "app:widgets", "norm:mode", "app:enabled");
        report.Entries.Should().OnlyContain(e => e.Reason != null && !e.Reason.Contains("secret-looking-text"));
        (await ValueAsync(provider, "app:smtp:port")).Should().Be("secret-looking-text");
    }

    [Fact]
    public async Task Normalize_Records_AreOnlyAcceptedWhenAlreadyValidJson() {
        using var provider = BuildHost();
        await SetAsync(provider, "app:widgets", "{\"maxItems\":5,\"title\":\"x\"}");
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var report = await normalizer.NormalizeAsync(SettingsScope.Global, cancellationToken: Ct);

        report.Entries.Should().ContainSingle().Which.Outcome.Should().Be(SettingNormalizationOutcome.AlreadyCanonical);
    }

    [Fact]
    public async Task Normalize_RemovesUnrecoverableRows_OnlyWhenAsked() {
        using var provider = BuildHost();
        await SetAsync(provider, "app:smtp:port", "not-a-number");
        await SetAsync(provider, "app:enabled", "True");
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var kept = await normalizer.NormalizeAsync(SettingsScope.Global, new SettingNormalizationOptions
            { KeyPrefix = "app:smtp" }, Ct);
        kept.Unreadable.Should().Be(1);
        (await ValueAsync(provider, "app:smtp:port")).Should().Be("not-a-number");

        var removed = await normalizer.NormalizeAsync(SettingsScope.Global, new SettingNormalizationOptions
            { RemoveUnrecoverable = true }, Ct);

        removed.Removed.Should().Be(1);
        removed.Rewritten.Should().Be(1);
        removed.Entries.Should().Contain(e => e.Key == "app:smtp:port" && e.Outcome == SettingNormalizationOutcome.Removed);
        (await provider.GetRequiredService<ISettingsStore>().GetAsync(SettingsScope.Global, "app:smtp:port", Ct))
            .Should().BeNull();
        var again = await normalizer.NormalizeAsync(SettingsScope.Global, cancellationToken: Ct);
        again.Rewritten.Should().Be(0);
        again.Removed.Should().Be(0);
    }

    [Fact]
    public async Task Normalize_KeepsAProtectedSecretOnItsScheme_AndNormalizesItsPlaintext() {
        var protector = new FakeSettingValueProtector();
        using var provider = BuildHost(protector);
        var scope = SettingsScope.Global;
        var store = provider.GetRequiredService<ISettingsStore>();
        var purpose = SettingValueCodec.CreatePurpose(scope, "norm:secretflag");
        await store.SetAsync(scope, "norm:secretflag", protector.Protect(purpose, "True"), protector.Scheme,
            cancellationToken: Ct);
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var report = await normalizer.NormalizeAsync(scope, cancellationToken: Ct);

        report.Rewritten.Should().Be(1);
        var entry = (await store.GetAsync(scope, "norm:secretflag", Ct))!.Value;
        entry.Protection.Should().Be(protector.Scheme);
        protector.Unprotect(purpose, entry.Value!).Plaintext.Should().Be("true");
    }

    [Fact]
    public async Task Normalize_DoesNotProtectAPlaintextSecret_BehindTheApplicationsBack() {
        var protector = new FakeSettingValueProtector();
        using var provider = SettingsTestHost.Build(protector: protector);
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SetAsync(SettingsScope.Global, "app:apikey", "raw-secret", null, cancellationToken: Ct);
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var report = await normalizer.NormalizeAsync(SettingsScope.Global, cancellationToken: Ct);

        report.Rewritten.Should().Be(1);
        protector.ProtectCalls.Should().Be(0);
        var entry = (await store.GetAsync(SettingsScope.Global, "app:apikey", Ct))!.Value;
        entry.Protection.Should().BeNull();
        entry.Value.Should().Be("\"raw-secret\"");
        await SettingsTestHost.Scoped<ISettingReprotector>(provider)
            .ReprotectAsync(SettingsScope.Global, cancellationToken: Ct);
        (await store.GetAsync(SettingsScope.Global, "app:apikey", Ct))!.Value.Protection.Should().Be(protector.Scheme);
        (await SettingsTestHost.Scoped<ISettingsManager>(provider)
            .GetAsync(TestSettings.ApiKey, cancellationToken: Ct)).Should().Be("raw-secret");
    }

    [Fact]
    public async Task Normalize_ReportsASecretWithAnUnknownScheme_AndNeverRemovesIt() {
        using var provider = SettingsTestHost.Build();
        await SetAsync(provider, "app:apikey", "opaque", "other.v9");
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var report = await normalizer.NormalizeAsync(SettingsScope.Global, new SettingNormalizationOptions
            { RemoveUnrecoverable = true }, Ct);

        report.Entries.Should().ContainSingle().Which.Outcome.Should().Be(SettingNormalizationOutcome.Unreadable);
        (await ValueAsync(provider, "app:apikey")).Should().Be("opaque");
    }

    [Fact]
    public async Task Normalize_SkipsAnEntryThatChangedConcurrently() {
        using var provider = SettingsTestHost.Build(customize: services =>
            services.AddSingleton<ISettingsStore>(sp => new RacingStore(
                new Elarion.Settings.InProcess.InProcessSettingsStore(
                    sp.GetRequiredService<ISettingsChangePublisher>(), TimeProvider.System))));
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);
        await provider.GetRequiredService<ISettingsStore>()
            .SetAsync(SettingsScope.Global, "app:title", "legacy", null, cancellationToken: Ct);

        var report = await normalizer.NormalizeAsync(SettingsScope.Global, cancellationToken: Ct);

        report.Skipped.Should().Be(1);
    }

    [Fact]
    public async Task Normalize_HonoursTheKeyPrefix() {
        using var provider = SettingsTestHost.Build();
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SetAsync(SettingsScope.Global, "app:title", "legacy", null, cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, "app:plain", "legacy", null, cancellationToken: Ct);
        var normalizer = SettingsTestHost.Scoped<ISettingNormalizer>(provider);

        var report = await normalizer.NormalizeAsync(SettingsScope.Global,
            new SettingNormalizationOptions { KeyPrefix = "app:title" }, Ct);

        report.Scanned.Should().Be(1);
        (await store.GetAsync(SettingsScope.Global, "app:plain", Ct))!.Value.Value.Should().Be("legacy");
    }
}

/// <summary>Bumps the entry's version just before a guarded write, as a concurrent writer would.</summary>
internal sealed class RacingStore(ISettingsStore inner) : ISettingsStore {
    public ValueTask<SettingEntry?> GetAsync(SettingsScope scope, string key, CancellationToken ct = default) {
        return inner.GetAsync(scope, key, ct);
    }

    public ValueTask<IReadOnlyList<SettingEntry>> GetAllAsync(SettingsScope scope, CancellationToken ct = default) {
        return inner.GetAllAsync(scope, ct);
    }

    public async ValueTask<SettingWriteResult> SetAsync(SettingsScope scope, string key, string? value,
        string? protection, int? expectedVersion = null, CancellationToken ct = default) {
        if (expectedVersion is not null)
            await inner.SetAsync(scope, key, "\"someone else\"", null, cancellationToken: ct);

        return await inner.SetAsync(scope, key, value, protection, expectedVersion, ct);
    }

    public ValueTask<bool> RemoveAsync(SettingsScope scope, string key, int? expectedVersion = null,
        CancellationToken ct = default) {
        return inner.RemoveAsync(scope, key, expectedVersion, ct);
    }
}

internal enum NormMode {
    Slow,
    Fast
}

[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(NormMode))]
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
internal sealed partial class NormJsonContext : JsonSerializerContext;
