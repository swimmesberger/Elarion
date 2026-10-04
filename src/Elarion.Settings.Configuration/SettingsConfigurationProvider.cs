using Microsoft.Extensions.Configuration;

namespace Elarion.Settings.Configuration;

/// <summary>
/// An <see cref="IConfigurationProvider"/> that is a <b>projection</b> of the settings resolver for the
/// <see cref="SettingsScope.Global"/> scope — the effective store and default values of the non-secret
/// definitions, flattened into <c>IConfiguration</c> keys for options binding. It is not a second source of
/// truth: the resolver never reads it back (<see cref="ISettingsProjectionProvider"/>), and a value that
/// deployment configuration pins is simply the configuration value already present.
/// </summary>
/// <remarks>
/// Authoring an <c>IConfiguration</c> provider is AOT-safe — it produces only a flat string key/value map; the
/// reflection cost lives entirely on the consuming side (<c>ConfigurationBinder.Get&lt;T&gt;()</c>), which is the
/// caller's opt-in. The data is pushed in by <see cref="SettingsConfigurationRefresher"/> after the DI container
/// exists (configuration is built before DI), so values appear once the host starts.
/// </remarks>
public sealed class SettingsConfigurationProvider : ConfigurationProvider, ISettingsProjectionProvider {
    /// <summary>
    /// Replaces the provider's data with <paramref name="data"/> and signals a configuration reload — only when
    /// the data actually changed, which also stops a refresh triggered by this provider's own reload from looping.
    /// The reload flows through to <c>IConfiguration.GetReloadToken()</c> and <c>IOptionsMonitor&lt;T&gt;</c>.
    /// </summary>
    public void Apply(IReadOnlyDictionary<string, string?> data) {
        ArgumentNullException.ThrowIfNull(data);

        // IConfiguration keys are case-insensitive; settings keys are already ':'-separated, so they map
        // straight onto the IConfiguration hierarchy.
        var next = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);
        if (Data.Count == next.Count && Data.All(pair => next.TryGetValue(pair.Key, out var value) &&
                                                         string.Equals(value, pair.Value, StringComparison.Ordinal)))
            return;

        Data = next;
        OnReload();
    }
}
