using AwesomeAssertions;
using Elarion.Abstractions.Serialization;
using Elarion.Settings;
using Elarion.Settings.DataProtection;
using Elarion.Settings.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// Secret settings over the real EF Core store on PostgreSQL with ASP.NET Core Data Protection: the row holds only
/// the protected payload plus the scheme column, and re-protection converges legacy plaintext. Skips when Docker is
/// unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SecretSettingsIntegrationTests(PostgreSqlSettingsStoreFixture fixture)
    : IClassFixture<PostgreSqlSettingsStoreFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceProvider BuildProvider() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddDbContext<SettingsIntegrationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(SettingsTestJsonContext.Default));
        services.AddElarionSettings(o => o.AddDefinitions(TestSettings.All));
        services.AddElarionSettingsEntityFrameworkCore<SettingsIntegrationDbContext>();
        services.AddElarionSettingsDataProtection(o => o.ReprotectOnStartup = false);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SecretValue_IsOnlyStoredProtected_WithSchemeColumn_AndReadsBackAsPlaintext() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        var owner = SettingsScope.User($"it-{Guid.CreateVersion7():N}");

        var write = await manager.SetAsync(TestSettings.UserToken, "s3cr3t", owner, cancellationToken: Ct);

        write.IsSuccess.Should().BeTrue();
        await using var context = fixture.CreateContext();
        var row = await context.Set<Setting>().SingleAsync(s => s.Owner == owner.Owner && s.Key == "user:token", Ct);
        row.Protection.Should().Be(DataProtectionSettingValueProtector.SchemeId);
        row.Value.Should().NotContain("s3cr3t");
        (await manager.GetAsync(TestSettings.UserToken, owner, Ct)).Should().Be("s3cr3t");
    }

    [Fact]
    public async Task Reprotect_ConvergesLegacyPlaintextInTheDatabase() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        var owner = SettingsScope.User($"it-{Guid.CreateVersion7():N}");
        await store.SetAsync(owner, "user:token", "\"plain\"", null, cancellationToken: Ct);
        var reprotector = scope.ServiceProvider.GetRequiredService<ISettingReprotector>();

        var first = await reprotector.ReprotectAsync(owner, cancellationToken: Ct);
        var second = await reprotector.ReprotectAsync(owner, cancellationToken: Ct);

        first.Reprotected.Should().Be(1);
        second.Reprotected.Should().Be(0);
        (await store.GetAsync(owner, "user:token", Ct))!.Value.Protection.Should()
            .Be(DataProtectionSettingValueProtector.SchemeId);
        (await scope.ServiceProvider.GetRequiredService<ISettingsManager>()
            .GetAsync(TestSettings.UserToken, owner, Ct)).Should().Be("plain");
    }
}
