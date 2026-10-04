using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Elarion.Settings.Configuration;

/// <summary>Wires settings-backed <c>IConfiguration</c> onto a host builder.</summary>
public static class SettingsConfigurationBuilderExtensions {
    /// <summary>
    /// Adds the settings resolver's global effective values to the host's <c>IConfiguration</c> as a live-reloading
    /// <i>projection</i> (secrets are never projected), so
    /// <c>IConfiguration</c>/<c>IOptionsMonitor&lt;T&gt;</c> consumers (and the scheduler's <c>${...}</c>
    /// variable substitution) observe runtime changes. Ensures the settings foundation is registered (the
    /// in-process backend by default); call <c>AddElarionSettingsEntityFrameworkCore</c> as well to use the
    /// database backend. Only the <see cref="SettingsScope.Global"/> scope is surfaced — per-user settings are
    /// not app-wide configuration; read those through <see cref="ISettingsManager"/>. Register this provider
    /// <b>after</b> the application's other configuration sources so the projected effective values are not masked
    /// by a later provider for the same key. The provider is a projection: the resolver never reads it back, so it
    /// cannot pin a definition.
    /// <para>
    /// The <see cref="SettingsConfigurationRefresher"/> performs its initial load in its
    /// <c>StartAsync</c>, so it completes before subsequently-registered hosted services start. Call this
    /// method <b>before</b> registering any hosted service that reads settings-backed <c>${...}</c>
    /// configuration at start (for example the scheduler via <c>AddElarionScheduler</c>), so those services
    /// observe the stored values rather than empty defaults.
    /// </para>
    /// </summary>
    public static TBuilder AddElarionSettingsConfiguration<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder {
        ArgumentNullException.ThrowIfNull(builder);

        // Ensure ISettingsStore + ISettingsChangeSource exist (TryAdd: a previously registered backend wins).
        builder.Services.AddElarionSettings();

        var source = new SettingsConfigurationSource();
        builder.Configuration.Add(source);

        // Share the provider instance the configuration system uses so the refresher pushes data into it.
        builder.Services.AddSingleton(source.Provider);
        builder.Services.AddHostedService<SettingsConfigurationRefresher>();

        return builder;
    }
}
