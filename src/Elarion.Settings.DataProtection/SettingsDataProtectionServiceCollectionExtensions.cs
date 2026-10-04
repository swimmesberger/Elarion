using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Settings.DataProtection;

/// <summary>Registers Data Protection as the protector for secret settings.</summary>
public static class SettingsDataProtectionServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="DataProtectionSettingValueProtector"/> as the <see cref="ISettingValueProtector"/> (an
    /// earlier registration of another protector wins), makes sure ASP.NET Core Data Protection is registered, and
    /// optionally re-protects existing secrets at startup. Secret definitions are declared with
    /// <c>[Setting(..., Secret = true)]</c>.
    /// </summary>
    /// <remarks>
    /// This calls <c>AddDataProtection()</c> with its defaults only if the application has not configured Data
    /// Protection. Production hosts must configure key persistence and sharing themselves (the default key ring
    /// is local to the machine and is lost with a container) — secrets protected under a lost key ring cannot be
    /// recovered.
    /// </remarks>
    public static IServiceCollection AddElarionSettingsDataProtection(
        this IServiceCollection services,
        Action<SettingsDataProtectionOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SettingsDataProtectionOptions();
        configure?.Invoke(options);

        services.AddElarionSettings();
        services.AddDataProtection();
        services.TryAddSingleton<ISettingValueProtector, DataProtectionSettingValueProtector>();

        if (options.ReprotectOnStartup) {
            services.TryAddSingleton(options);
            services.AddHostedService<SettingsReprotectionHostedService>();
        }

        return services;
    }
}
