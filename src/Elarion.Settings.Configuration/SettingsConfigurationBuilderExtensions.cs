using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Elarion.Settings.Configuration;

/// <summary>Wires settings-backed <c>IConfiguration</c> onto a host builder.</summary>
public static class SettingsConfigurationBuilderExtensions {
    /// <summary>
    /// Adds the global settings to the host's <c>IConfiguration</c> as a live-reloading provider, so
    /// <c>IConfiguration</c>/<c>IOptionsMonitor&lt;T&gt;</c> consumers (and the scheduler's <c>${...}</c>
    /// variable substitution) observe runtime changes. Ensures the settings foundation is registered (the
    /// in-process backend by default); call <c>AddElarionSettingsEntityFrameworkCore</c> as well to use the
    /// database backend. Only the <see cref="SettingsScope.Global"/> scope is surfaced — per-user settings are
    /// not app-wide configuration; read those through <see cref="ISettingsManager"/>.
    /// <para>
    /// The <see cref="SettingsConfigurationRefresher"/> performs its initial load in its
    /// <c>StartAsync</c>, so it completes before subsequently-registered hosted services start. Call this
    /// method <b>before</b> registering any hosted service that reads settings-backed <c>${...}</c>
    /// configuration at start (for example the scheduler via <c>AddInMemoryScheduler</c>), so those services
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
