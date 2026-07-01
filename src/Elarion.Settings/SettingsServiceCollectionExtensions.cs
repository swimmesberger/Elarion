using Elarion.Abstractions.Serialization;
using Elarion.Settings.InProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Settings;

/// <summary>Registers the Elarion settings subsystem.</summary>
public static class SettingsServiceCollectionExtensions {
    /// <summary>
    /// Registers the settings foundation: the in-process store and change source (the shipped default sink)
    /// and the scoped <see cref="ISettingsManager"/> accessor. Swap the sink later by registering a different
    /// <see cref="ISettingsStore"/> (for example the EF Core provider) before or after this call —
    /// the store registration here uses <c>TryAdd</c> so an earlier registration wins.
    /// </summary>
    public static IServiceCollection AddElarionSettings(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElarionJson();
        services.TryAddSingleton(TimeProvider.System);

        // One in-process instance backs both the watch (source) and signal (publisher) seams.
        services.TryAddSingleton<InProcessSettingsChangeSource>();
        services.TryAddSingleton<ISettingsChangeSource>(sp => sp.GetRequiredService<InProcessSettingsChangeSource>());
        services.TryAddSingleton<ISettingsChangePublisher>(sp => sp.GetRequiredService<InProcessSettingsChangeSource>());

        services.TryAddSingleton<ISettingsStore, InProcessSettingsStore>();

        // Scoped so it resolves the current request's ICurrentUser for user-scoped reads.
        services.TryAddScoped<ISettingsManager, SettingsManager>();

        return services;
    }
}
