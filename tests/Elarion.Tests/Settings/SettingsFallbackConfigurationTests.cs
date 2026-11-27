using AwesomeAssertions;
using Elarion.Abstractions.Serialization;
using Elarion.Abstractions.Settings;
using Elarion.Settings;
using Elarion.Settings.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// A <see cref="SettingDefinition.FallbackConfigurationKey"/> puts deployment configuration <b>under</b> the stored
/// value: stored, else configured at the fallback key, else the default — and, unlike a pin, it never refuses a write.
/// </summary>
public sealed class SettingsFallbackConfigurationTests {
    private static readonly SettingDefinition<string> Host = new(
        "mail:smtp:host", new SettingDefinitionOptions { FallbackConfigurationKey = "Email:Smtp:Host" }, "localhost");

    private static readonly SettingDefinition<int> Port = new(
        "mail:smtp:port", new SettingDefinitionOptions { FallbackConfigurationKey = "Email:Smtp:Port" }, 25);

    private static readonly SettingDefinition<string> Signature = new(
        "mail:signature",
        new SettingDefinitionOptions { Scopes = ["global", "user"], FallbackConfigurationKey = "Email:Signature" });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(params (string Key, string? Value)[] configuration) {
        return SettingsTestHost.Build(configuration.ToDictionary(v => v.Key, v => v.Value),
            configure: o => o.AddDefinitions([Host, Port, Signature]));
    }

    [Fact]
    public async Task Layers_DefaultThenConfigurationFallbackThenStore() {
        using var provider = Build(("Email:Smtp:Host", "mail.internal"), ("Email:Smtp:Port", "2525"));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        var configured = await manager.GetResolvedAsync(Host, cancellationToken: Ct);
        configured.Value.Should().Be("mail.internal");
        configured.Source.Should().Be(SettingSource.ConfigurationFallback);
        configured.IsPinned.Should().BeFalse();
        (await manager.GetAsync(Port, cancellationToken: Ct)).Should().Be(2525);

        (await manager.SetAsync(Host, "smtp.example.com", cancellationToken: Ct)).IsSuccess
            .Should().BeTrue("a fallback never refuses a write");
        var stored = await manager.GetResolvedAsync(Host, cancellationToken: Ct);
        stored.Value.Should().Be("smtp.example.com");
        stored.Source.Should().Be(SettingSource.Store);

        await manager.ResetAsync(Host, cancellationToken: Ct);
        (await manager.GetAsync(Host, cancellationToken: Ct)).Should().Be("mail.internal", "a reset falls back again");
    }

    [Fact]
    public async Task AFallbackValue_IsSet_ButNotStored() {
        using var provider = Build(("Email:Smtp:Host", "mail.internal"));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        var configured = await manager.GetResolvedAsync(Host, cancellationToken: Ct);
        configured.IsSet.Should().BeTrue("the value is not the default");
        configured.IsStored.Should().BeFalse("nothing is stored");

        await manager.SetAsync(Host, "smtp.example.com", cancellationToken: Ct);
        var stored = await manager.GetResolvedAsync(Host, cancellationToken: Ct);
        stored.IsSet.Should().BeTrue();
        stored.IsStored.Should().BeTrue();

        var defaulted = await manager.GetResolvedAsync(Port, cancellationToken: Ct);
        defaulted.IsSet.Should().BeFalse();
        defaulted.IsStored.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AMissingOrBlankFallbackValue_LeavesTheDefault(string? configured) {
        using var provider = Build(("Email:Smtp:Host", configured));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        var resolved = await manager.GetResolvedAsync(Host, cancellationToken: Ct);

        resolved.Value.Should().Be("localhost");
        resolved.Source.Should().Be(SettingSource.Default);
    }

    [Fact]
    public async Task TheFallback_AppliesInTheGlobalScopeOnly() {
        using var provider = Build(("Email:Signature", "Regards"));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        (await manager.GetAsync(Signature, cancellationToken: Ct)).Should().Be("Regards");
        (await manager.GetAsync(Signature, SettingsScope.User("u1"), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Describe_ReportsTheFallbackValueAndItsSource() {
        using var provider = Build(("Email:Smtp:Host", "mail.internal"));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        var host = (await manager.DescribeAsync(keyPrefix: "mail:smtp:host", cancellationToken: Ct)).Single();

        host.Source.Should().Be(SettingSource.ConfigurationFallback);
        host.ValueJson.Should().Be("\"mail.internal\"");
        host.IsPinned.Should().BeFalse();
    }

    [Fact]
    public async Task Projection_CarriesTheFallbackValueAtTheSettingsKey_UntilAValueIsStored() {
        using var provider = Build(("Email:Smtp:Host", "mail.internal"));
        var resolver = SettingsTestHost.Scoped<ISettingResolver>(provider);
        var serialization = provider.GetRequiredService<IElarionJsonSerialization>();
        var deployment = provider.GetRequiredService<IConfiguration>();

        var before = SettingsConfigurationProjection.Project(
            await resolver.ResolveAllAsync(SettingsScope.Global, "mail", Ct), serialization, deployment);
        await SettingsTestHost.Scoped<ISettingsManager>(provider).SetAsync(Host, "smtp.example.com", cancellationToken: Ct);
        var after = SettingsConfigurationProjection.Project(
            await resolver.ResolveAllAsync(SettingsScope.Global, "mail", Ct), serialization, deployment);

        before.Data["mail:smtp:host"].Should().Be("mail.internal");
        after.Data["mail:smtp:host"].Should().Be("smtp.example.com");
    }

    [Fact]
    public void Watch_FiresWhenTheDeploymentConfigurationReloads() {
        var configuration = new ConfigurationManager();
        using var provider = SettingsTestHost.Build(configurationRoot: configuration,
            configure: o => o.AddDefinitions([Host, Port, Signature]));
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        var token = manager.Watch(Host);
        ((IConfigurationBuilder)configuration).AddInMemoryCollection(
            new Dictionary<string, string?> { ["Email:Smtp:Host"] = "mail.internal" });

        token.HasChanged.Should().BeTrue();
    }
}
