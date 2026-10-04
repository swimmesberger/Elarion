using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.DataProtection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class DataProtectionSettingsTests : IDisposable {
    private readonly DirectoryInfo _keyRing = Directory.CreateTempSubdirectory("elarion-settings-keys-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() {
        _keyRing.Delete(true);
    }

    private ServiceProvider BuildProvider(
        Action<SettingsDataProtectionOptions>? configure = null,
        ISettingsStore? shareStoreWith = null) {
        return SettingsTestHost.Build(
            withoutProtector: true,
            customize: services => {
                services.AddLogging();
                if (shareStoreWith is not null) services.AddSingleton(shareStoreWith);
                services.AddDataProtection().SetApplicationName("elarion-tests").PersistKeysToFileSystem(_keyRing);
                services.AddElarionSettingsDataProtection(configure);
            });
    }

    [Fact]
    public async Task SecretSetting_RoundTripsAndIsEncryptedAtRest() {
        using var provider = BuildProvider();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        await manager.SetAsync(TestSettings.ApiKey, "p@ssw0rd", cancellationToken: Ct);

        var stored = await provider.GetRequiredService<ISettingsStore>()
            .GetAsync(SettingsScope.Global, "app:apikey", Ct);
        stored!.Value.Protection.Should().Be(DataProtectionSettingValueProtector.SchemeId);
        stored.Value.Value.Should().NotContain("p@ssw0rd");
        (await manager.GetAsync(TestSettings.ApiKey, cancellationToken: Ct)).Should().Be("p@ssw0rd");
    }

    [Fact]
    public void Protector_IsolatesPurposes() {
        using var provider = BuildProvider();
        var protector = provider.GetRequiredService<ISettingValueProtector>();
        var payload = protector.Protect("purpose-a", "value");

        protector.Unprotect("purpose-a", payload).Plaintext.Should().Be("value");
        var act = () => protector.Unprotect("purpose-b", payload);
        act.Should().Throw<SettingProtectionException>();
    }

    [Fact]
    public void Protector_RejectsTamperedAndMalformedPayloads() {
        using var provider = BuildProvider();
        var protector = provider.GetRequiredService<ISettingValueProtector>();
        var payload = protector.Protect("purpose", "value");
        var tampered = payload[..^2] + (payload[^2] == 'A' ? "BB" : "AA");

        var tamperedAct = () => protector.Unprotect("purpose", tampered);
        var malformedAct = () => protector.Unprotect("purpose", "not base64 !!");

        tamperedAct.Should().Throw<SettingProtectionException>();
        malformedAct.Should().Throw<SettingProtectionException>();
    }

    [Fact]
    public void Protector_DoesNotFlagCurrentKeyPayloads() {
        using var provider = BuildProvider();
        var protector = provider.GetRequiredService<ISettingValueProtector>();

        protector.Unprotect("purpose", protector.Protect("purpose", "value")).RequiresReprotection.Should().BeFalse();
    }

    [Fact]
    public async Task RevokedKey_StaysReadable_AndIsReprotectedUnderTheCurrentKey() {
        using var first = BuildProvider();
        var store = first.GetRequiredService<ISettingsStore>();
        await SettingsTestHost.Scoped<ISettingsManager>(first).SetAsync(TestSettings.ApiKey, "value", cancellationToken: Ct);
        var before = await store.GetAsync(SettingsScope.Global, "app:apikey", Ct);
        first.GetRequiredService<IKeyManager>().RevokeAllKeys(DateTimeOffset.UtcNow, "test rotation");

        // A fresh process (its own key-ring cache) over the same key ring sees the revoked key.
        using var second = BuildProvider(shareStoreWith: store);
        var manager = SettingsTestHost.Scoped<ISettingsManager>(second);
        var reprotector = SettingsTestHost.Scoped<ISettingReprotector>(second);

        (await manager.GetAsync(TestSettings.ApiKey, cancellationToken: Ct)).Should().Be("value");
        var firstRun = await reprotector.ReprotectAsync(SettingsScope.Global, cancellationToken: Ct);
        var secondRun = await reprotector.ReprotectAsync(SettingsScope.Global, cancellationToken: Ct);

        firstRun.Reprotected.Should().Be(1);
        secondRun.Reprotected.Should().Be(0);
        (await store.GetAsync(SettingsScope.Global, "app:apikey", Ct))!.Value.Value.Should().NotBe(before!.Value.Value);
        (await manager.GetAsync(TestSettings.ApiKey, cancellationToken: Ct)).Should().Be("value");
    }

    [Fact]
    public async Task HostedService_ReprotectsLegacyPlaintextAtStartup() {
        using var provider = BuildProvider();
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SetAsync(SettingsScope.Global, "app:apikey", "\"plain\"", null, cancellationToken: Ct);

        await provider.GetServices<IHostedService>().OfType<SettingsReprotectionHostedService>().Single().StartAsync(Ct);

        (await store.GetAsync(SettingsScope.Global, "app:apikey", Ct))!.Value.Protection.Should()
            .Be(DataProtectionSettingValueProtector.SchemeId);
    }

    [Fact]
    public void ReprotectOnStartupDisabled_RegistersNoHostedService() {
        using var provider = BuildProvider(o => o.ReprotectOnStartup = false);

        provider.GetServices<IHostedService>().Should().NotContain(h => h is SettingsReprotectionHostedService);
    }

    [Fact]
    public void ExistingProtectorRegistration_Wins() {
        var services = new ServiceCollection();
        var custom = new FakeSettingValueProtector();
        services.AddSingleton<ISettingValueProtector>(custom);
        services.AddElarionSettingsDataProtection(o => o.ReprotectOnStartup = false);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISettingValueProtector>().Should().BeSameAs(custom);
    }
}
