using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Serialization;
using Elarion.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Tests.Settings;

/// <summary>Builds settings service providers for the resolver, manager, secret, and projection tests.</summary>
internal static class SettingsTestHost {
    public static ServiceProvider Build(
        IReadOnlyDictionary<string, string?>? configuration = null,
        ICurrentUser? currentUser = null,
        ISettingValueProtector? protector = null,
        Action<SettingsOptions>? configure = null,
        Action<IServiceCollection>? customize = null,
        IConfiguration? configurationRoot = null,
        bool withoutProtector = false) {
        var services = new ServiceCollection();
        if (currentUser is not null) services.AddSingleton(currentUser);

        if (configurationRoot is not null) services.AddSingleton(configurationRoot);
        else if (configuration is not null)
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build());

        if (!withoutProtector) services.AddSingleton(protector ?? new FakeSettingValueProtector());

        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(SettingsTestJsonContext.Default));
        services.AddElarionSettings(o => {
            o.AddDefinitions(TestSettings.All);
            configure?.Invoke(o);
        });
        customize?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public static T Scoped<T>(ServiceProvider provider) where T : notnull {
        return provider.CreateScope().ServiceProvider.GetRequiredService<T>();
    }
}

/// <summary>A reversible protector for tests: the payload is the reversed text, bound to the purpose.</summary>
internal sealed class FakeSettingValueProtector : ISettingValueProtector {
    public int ProtectCalls { get; private set; }

    public bool ReportRequiresReprotection { get; set; }

    public string Scheme { get; set; } = "fake.v1";

    public string Protect(string purpose, string plaintext) {
        ProtectCalls++;
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(purpose + "|" + plaintext));
    }

    public SettingUnprotectResult Unprotect(string purpose, string payload) {
        string text;
        try {
            text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        }
        catch (FormatException ex) {
            throw new SettingProtectionException("bad payload", ex);
        }

        if (!text.StartsWith(purpose + "|", StringComparison.Ordinal))
            throw new SettingProtectionException("purpose mismatch");

        return new SettingUnprotectResult(text[(purpose.Length + 1)..], ReportRequiresReprotection);
    }
}
