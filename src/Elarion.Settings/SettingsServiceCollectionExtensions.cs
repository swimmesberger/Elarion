using Elarion.Abstractions.Serialization;
using Elarion.Settings.InProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Elarion.Settings;

/// <summary>Registers the Elarion settings subsystem.</summary>
public static class SettingsServiceCollectionExtensions {
    /// <summary>
    /// Registers the settings foundation: the definition catalog, the effective-value resolver, the in-process
    /// store and change source (the shipped default sink), the scoped <see cref="ISettingsManager"/>, the
    /// re-protector, the legacy-value normalizer, and a startup check that fails the host when a secret definition has no
    /// <see cref="ISettingValueProtector"/>. Swap the sink by registering a different <see cref="ISettingsStore"/>
    /// (for example the EF Core provider) before or after this call — the store registration here uses
    /// <c>TryAdd</c> so an earlier registration wins. Safe to call repeatedly; the options accumulate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Configures <see cref="SettingsOptions"/>, typically
    /// <c>o =&gt; o.AddDefinitions(ElarionSettingDefinitions.All)</c>.
    /// </param>
    public static IServiceCollection AddElarionSettings(
        this IServiceCollection services, Action<SettingsOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.FirstOrDefault(static d => d.ServiceType == typeof(SettingsOptions))
            ?.ImplementationInstance as SettingsOptions;
        if (options is null) {
            options = new SettingsOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(options);

        services.AddElarionJson();
        services.TryAddSingleton(TimeProvider.System);

        // One in-process instance backs both the watch (source) and signal (publisher) seams.
        services.TryAddSingleton<InProcessSettingsChangeSource>();
        services.TryAddSingleton<ISettingsChangeSource>(sp => sp.GetRequiredService<InProcessSettingsChangeSource>());
        services.TryAddSingleton<ISettingsChangePublisher>(sp =>
            sp.GetRequiredService<InProcessSettingsChangeSource>());

        services.TryAddSingleton<ISettingsStore, InProcessSettingsStore>();
        services.TryAddSingleton<ISettingDefinitionCatalog, SettingDefinitionCatalog>();

        // The pin check needs only the catalog and IConfiguration, so it is singleton-safe.
        services.TryAddSingleton<ISettingPins, SettingPins>();

        // Scoped: the store may be scoped (EF Core), and the manager resolves the current request's ICurrentUser.
        services.TryAddScoped<ISettingResolver, SettingResolver>();
        services.TryAddScoped<ISettingsManager, SettingsManager>();
        services.TryAddScoped<ISettingReprotector, SettingReprotector>();
        services.TryAddScoped<ISettingNormalizer, SettingNormalizer>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SettingsStartupValidator>());
        return services;
    }
}
