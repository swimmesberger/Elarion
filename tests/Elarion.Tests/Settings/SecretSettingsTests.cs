using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Tests.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class SecretSettingsTests {
    private const string Secret = "super-secret-value";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SecretValue_IsStoredProtected_WithTheSchemeAsStoreMetadata() {
        var protector = new FakeSettingValueProtector();
        using var provider = SettingsTestHost.Build(protector: protector);
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var store = provider.GetRequiredService<ISettingsStore>();

        await manager.SetAsync(TestSettings.ApiKey, Secret, cancellationToken: Ct);

        var entry = await store.GetAsync(SettingsScope.Global, "app:apikey", Ct);
        entry.Should().NotBeNull();
        entry!.Value.Protection.Should().Be("fake.v1");
        entry.Value.Value.Should().NotContain(Secret);
        (await manager.GetAsync(TestSettings.ApiKey, cancellationToken: Ct)).Should().Be(Secret);
    }

    [Fact]
    public async Task NonSecretValue_IsStoredAsPlainJson_WithoutProtection() {
        var protector = new FakeSettingValueProtector();
        using var provider = SettingsTestHost.Build(protector: protector);
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var store = provider.GetRequiredService<ISettingsStore>();

        await manager.SetAsync(TestSettings.Title, "visible", cancellationToken: Ct);

        var entry = await store.GetAsync(SettingsScope.Global, "app:title", Ct);
        entry!.Value.Protection.Should().BeNull();
        entry.Value.Value.Should().Be("\"visible\"");
        protector.ProtectCalls.Should().Be(0);
    }

    [Fact]
    public async Task ProtectedPayload_DoesNotUnprotectForAnotherOwnerOrKey() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var store = provider.GetRequiredService<ISettingsStore>();
        await manager.SetAsync(TestSettings.UserToken, Secret, SettingsScope.User("u1"), cancellationToken: Ct);
        await manager.SetAsync(TestSettings.ApiKey, Secret, cancellationToken: Ct);
        var userEntry = (await store.GetAsync(SettingsScope.User("u1"), "user:token", Ct))!.Value;
        var globalEntry = (await store.GetAsync(SettingsScope.Global, "app:apikey", Ct))!.Value;

        // The same ciphertext copied onto another owner's row, and a global ciphertext copied onto a user row.
        await store.SetAsync(SettingsScope.User("u2"), "user:token", userEntry.Value, userEntry.Protection,
            cancellationToken: Ct);
        await store.SetAsync(SettingsScope.User("u3"), "user:token", globalEntry.Value, globalEntry.Protection,
            cancellationToken: Ct);

        var otherOwner = async () => await manager.GetAsync(TestSettings.UserToken, SettingsScope.User("u2"), Ct);
        var otherKey = async () => await manager.GetAsync(TestSettings.UserToken, SettingsScope.User("u3"), Ct);
        await otherOwner.Should().ThrowAsync<SettingProtectionException>();
        await otherKey.Should().ThrowAsync<SettingProtectionException>();
        (await manager.GetAsync(TestSettings.UserToken, SettingsScope.User("u1"), Ct)).Should().Be(Secret);
    }

    [Fact]
    public void SecretDefinitionWithoutProtector_FailsClosed_AtCatalogResolutionAndStartup() {
        using var provider = SettingsTestHost.Build(withoutProtector: true);

        var catalog = () => provider.GetRequiredService<ISettingDefinitionCatalog>();
        var startup = () => provider.GetServices<IHostedService>().ToList();

        catalog.Should().Throw<SettingProtectionException>().WithMessage("*app:apikey*ISettingValueProtector*");
        startup.Should().Throw<SettingProtectionException>();
    }

    [Fact]
    public async Task SchemeMismatch_FailsInsteadOfGuessing() {
        var protector = new FakeSettingValueProtector();
        using var provider = SettingsTestHost.Build(protector: protector);
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        await manager.SetAsync(TestSettings.ApiKey, Secret, cancellationToken: Ct);

        protector.Scheme = "fake.v2";

        var act = async () => await manager.GetAsync(TestSettings.ApiKey, cancellationToken: Ct);
        await act.Should().ThrowAsync<SettingProtectionException>().WithMessage("*fake.v1*fake.v2*");
    }

    [Fact]
    public async Task Describe_NeverReturnsASecretValue_ButReportsItExists() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        await manager.SetAsync(TestSettings.ApiKey, Secret, cancellationToken: Ct);

        var description = (await manager.DescribeAsync(cancellationToken: Ct)).Single(d => d.Key == "app:apikey");

        description.IsSecret.Should().BeTrue();
        description.HasValue.Should().BeTrue();
        description.ValueJson.Should().BeNull();
        description.Source.Should().Be(SettingSource.Store);
        description.IsUnreadable.Should().BeFalse();
    }

    [Fact]
    public async Task Describe_FlagsAnUnreadableSecret_WithoutFailingTheWholeCall() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SetAsync(SettingsScope.Global, "app:apikey", "!!corrupt!!", "fake.v1", cancellationToken: Ct);

        var descriptions = await manager.DescribeAsync(cancellationToken: Ct);

        var secret = descriptions.Single(d => d.Key == "app:apikey");
        secret.IsUnreadable.Should().BeTrue();
        secret.HasValue.Should().BeFalse();
        descriptions.Single(d => d.Key == "app:title").Source.Should().Be(SettingSource.Default);
    }

    [Fact]
    public async Task Reprotect_ConvergesLegacyPlaintext_AndIsIdempotent() {
        var protector = new FakeSettingValueProtector();
        using var provider = SettingsTestHost.Build(protector: protector);
        var store = provider.GetRequiredService<ISettingsStore>();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var reprotector = SettingsTestHost.Scoped<ISettingReprotector>(provider);
        await store.SetAsync(SettingsScope.Global, "app:apikey", "\"legacy\"", null, cancellationToken: Ct);

        (await manager.GetAsync(TestSettings.ApiKey, cancellationToken: Ct)).Should().Be("legacy");
        var first = await reprotector.ReprotectAsync(SettingsScope.Global, cancellationToken: Ct);
        var second = await reprotector.ReprotectAsync(SettingsScope.Global, cancellationToken: Ct);

        first.Should().BeEquivalentTo(new SettingReprotectionReport { Scanned = 1, Reprotected = 1, Skipped = 0, Failed = 0 });
        second.Reprotected.Should().Be(0);
        var entry = (await store.GetAsync(SettingsScope.Global, "app:apikey", Ct))!.Value;
        entry.Protection.Should().Be("fake.v1");
        entry.Value.Should().NotContain("legacy");
        (await manager.GetAsync(TestSettings.ApiKey, cancellationToken: Ct)).Should().Be("legacy");
    }

    [Fact]
    public async Task Reprotect_RewritesPayloadsTheProtectorFlagsAsStale() {
        var protector = new FakeSettingValueProtector();
        using var provider = SettingsTestHost.Build(protector: protector);
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var reprotector = SettingsTestHost.Scoped<ISettingReprotector>(provider);
        await manager.SetAsync(TestSettings.ApiKey, Secret, cancellationToken: Ct);

        protector.ReportRequiresReprotection = true;
        var stale = await reprotector.ReprotectAsync(SettingsScope.Global, cancellationToken: Ct);
        protector.ReportRequiresReprotection = false;
        var converged = await reprotector.ReprotectAsync(SettingsScope.Global, cancellationToken: Ct);

        stale.Reprotected.Should().Be(1);
        converged.Reprotected.Should().Be(0);
        (await manager.GetAsync(TestSettings.ApiKey, cancellationToken: Ct)).Should().Be(Secret);
    }

    [Fact]
    public async Task Reprotect_CountsUnreadableEntries_IgnoresUndeclaredAndNonSecret() {
        using var provider = SettingsTestHost.Build();
        var store = provider.GetRequiredService<ISettingsStore>();
        var reprotector = SettingsTestHost.Scoped<ISettingReprotector>(provider);
        await store.SetAsync(SettingsScope.Global, "app:apikey", "!!corrupt!!", "fake.v1", cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, "app:title", "\"x\"", null, cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, "not:declared", "\"x\"", null, cancellationToken: Ct);

        var report = await reprotector.ReprotectAsync(SettingsScope.Global, cancellationToken: Ct);

        report.Should().BeEquivalentTo(new SettingReprotectionReport { Scanned = 1, Reprotected = 0, Skipped = 0, Failed = 1 });
    }

    [Fact]
    public async Task Reprotect_RejectsTheCurrentUserPlaceholder() {
        using var provider = SettingsTestHost.Build(currentUser: new FakeCurrentUser { IsAuthenticated = true });
        var reprotector = SettingsTestHost.Scoped<ISettingReprotector>(provider);

        var act = async () => await reprotector.ReprotectAsync(SettingsScope.CurrentUser, cancellationToken: Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
