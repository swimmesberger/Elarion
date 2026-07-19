using Microsoft.Extensions.Configuration;

namespace Elarion.Settings.Configuration;

/// <summary>
/// An <see cref="IConfigurationProvider"/> whose data is the <see cref="SettingsScope.Global"/> settings.
/// Authoring an <c>IConfiguration</c> provider is AOT-safe — it produces only a flat string key/value map;
/// the reflection cost lives entirely on the consuming side (<c>ConfigurationBinder.Get&lt;T&gt;()</c>),
/// which is the caller's opt-in. The data is pushed in by <see cref="SettingsConfigurationRefresher"/> after
/// the DI container exists (configuration is built before DI), so values appear once the host starts.
/// </summary>
public sealed class SettingsConfigurationProvider : ConfigurationProvider {
    /// <summary>
    /// Replaces the provider's data with <paramref name="entries"/> and signals a configuration reload, which
    /// flows through to <c>IConfiguration.GetReloadToken()</c> and <c>IOptionsMonitor&lt;T&gt;</c>.
    /// </summary>
    public void Apply(IReadOnlyList<SettingEntry> entries) {
        ArgumentNullException.ThrowIfNull(entries);

        // IConfiguration keys are case-insensitive; settings keys are already ':'-separated, so they map
        // straight onto the IConfiguration hierarchy.
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) data[entry.Key] = entry.Value;

        Data = data;
        OnReload();
    }
}
