using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class EfCoreSettingsStoreRegistrationTests {
    [Fact]
    public void AddElarionSettingsEntityFrameworkCore_RegistersEfStoreAsSettingsStore() {
        var services = new ServiceCollection();
        services.AddDbContext<SettingsIntegrationDbContext>(options =>
            options.UseNpgsql("Host=localhost;Database=elarion;Username=elarion;Password=elarion"));

        services.AddElarionSettingsEntityFrameworkCore<SettingsIntegrationDbContext>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

        // The EF store replaces the in-process default regardless of registration order.
        store.Should().BeOfType<EfCoreSettingsStore<SettingsIntegrationDbContext>>();
    }

    [Fact]
    public void AddElarionSettingsEntityFrameworkCore_StillRegistersManagerAndChangeSource() {
        var services = new ServiceCollection();
        services.AddDbContext<SettingsIntegrationDbContext>(options =>
            options.UseNpgsql("Host=localhost;Database=elarion;Username=elarion;Password=elarion"));

        services.AddElarionSettingsEntityFrameworkCore<SettingsIntegrationDbContext>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetService<ISettingsManager>().Should().NotBeNull();
        scope.ServiceProvider.GetService<ISettingsChangeSource>().Should().NotBeNull();
    }
}
