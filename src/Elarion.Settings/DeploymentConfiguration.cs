using Microsoft.Extensions.Configuration;

namespace Elarion.Settings;

/// <summary>
/// Reads a key from the <i>deployment</i> configuration: every configuration provider except the settings
/// projection (<see cref="ISettingsProjectionProvider"/>), so a value derived from the resolver is never mistaken for
/// one the deployment supplied. Shared by the pin check and the projection, which must agree on what
/// "configured" means.
/// </summary>
internal static class DeploymentConfiguration {
    /// <summary>The raw text the deployment configures at <paramref name="key"/>, or <see langword="null"/>.</summary>
    public static string? Read(IConfiguration configuration, string key) {
        if (configuration is not IConfigurationRoot root) return configuration[key];

        foreach (var provider in root.Providers.Reverse()) {
            if (provider is ISettingsProjectionProvider) continue;

            if (provider.TryGet(key, out var raw)) return raw;
        }

        return null;
    }

    /// <summary>
    /// Whether the deployment configures a usable value at <paramref name="key"/>: present and — unless
    /// <paramref name="emptyValuesCount"/> — neither empty nor whitespace (a blank environment variable or an
    /// unfilled template placeholder means "not set").
    /// </summary>
    public static string? ReadValue(IConfiguration configuration, string key, bool emptyValuesCount) {
        var raw = Read(configuration, key);
        if (raw is null) return null;

        return !emptyValuesCount && string.IsNullOrWhiteSpace(raw) ? null : raw;
    }
}
