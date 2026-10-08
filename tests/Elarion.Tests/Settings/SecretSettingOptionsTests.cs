using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class SecretSettingOptionsTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ApiClientOptions {
        public string? BaseUrl { get; set; }

        public string? ApiKey { get; set; }
    }

    [Fact]
    public async Task BindSecretSetting_SetsTheSecretOnOptions_FollowsChanges_AndKeepsItOutOfConfiguration() {
        var root = new ConfigurationManager();
        ((IConfigurationBuilder)root).AddInMemoryCollection(new Dictionary<string, string?> {
            ["client:baseUrl"] = "https://api.example", ["client:apiKey"] = "from-appsettings"
        });
        var projection = new SettingsConfigurationSource();
        ((IConfigurationBuilder)root).Add(projection);
        using var provider = SettingsTestHost.Build(configurationRoot: root, customize: services =>
            services.AddOptions<ApiClientOptions>()
                .Configure(o => {
                    o.BaseUrl = root["client:baseUrl"];
                    o.ApiKey = root["client:apiKey"];
                })
                .BindSecretSetting(TestSettings.ApiKey, static (o, key) => o.ApiKey = key));
        var refresher = new SettingsConfigurationRefresher(projection.Provider,
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<ISettingsChangeSource>(),
            root, NullLogger<SettingsConfigurationRefresher>.Instance);
        var monitor = provider.GetRequiredService<IOptionsMonitor<ApiClientOptions>>();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        await refresher.RefreshAsync(Ct);
        monitor.CurrentValue.ApiKey.Should().Be("from-appsettings", "an unset secret leaves the member alone");

        await manager.SetAsync(TestSettings.ApiKey, "first-secret", cancellationToken: Ct);
        await refresher.RefreshAsync(Ct);
        monitor.CurrentValue.ApiKey.Should().Be("first-secret");
        monitor.CurrentValue.BaseUrl.Should().Be("https://api.example");

        await manager.SetAsync(TestSettings.ApiKey, "rotated-secret", cancellationToken: Ct);
        await refresher.RefreshAsync(Ct);
        monitor.CurrentValue.ApiKey.Should().Be("rotated-secret");

        projection.Provider.TryGet("app:apikey", out _).Should().BeFalse();
        root.AsEnumerable().Select(pair => pair.Value).Should().NotContain("rotated-secret");
    }

    [Fact]
    public void BindSecretSetting_RejectsANonSecretDefinition() {
        var services = new ServiceCollection();

        var act = () => services.AddOptions<ApiClientOptions>()
            .BindSecretSetting(TestSettings.Title, static (o, title) => o.ApiKey = title);

        act.Should().Throw<ArgumentException>().WithMessage("*app:title*not a secret*");
    }
}
