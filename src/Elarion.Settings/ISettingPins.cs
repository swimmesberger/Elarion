using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Elarion.Settings;

/// <summary>
/// The deployment-configuration layer of the resolver, separated from the store-backed part so it can be used
/// anywhere: it needs only the definition catalog and <c>IConfiguration</c>, never the store, so it is registered
/// as a <b>singleton</b> and is safe to inject into singletons, middleware and options setup — unlike
/// <see cref="ISettingResolver"/>, which is scoped because the store may be.
/// </summary>
/// <remarks>
/// A configuration value pins a definition when the definition is <c>Pinnable</c>, the scope is
/// <see cref="SettingsScope.Global"/>, and the value is present and — unless
/// <see cref="SettingsOptions.EmptyConfigurationValuesPin"/> is set — neither empty nor whitespace. Only
/// non-projection configuration providers are consulted (see <see cref="ISettingsProjectionProvider"/>).
/// </remarks>
public interface ISettingPins {
    /// <summary>Whether deployment configuration currently pins <paramref name="definition"/> in <paramref name="scope"/>.</summary>
    /// <exception cref="InvalidOperationException">The definition is not registered.</exception>
    bool IsPinned(SettingDefinition definition, SettingsScope scope);

    /// <summary>
    /// Reads the raw configuration text that pins <paramref name="definition"/> in the global scope, or
    /// <see langword="null"/> when nothing pins it.
    /// </summary>
    string? GetPinnedText(SettingDefinition definition);

    /// <summary>A token that fires when the deployment configuration reloads, or <see langword="null"/> without one.</summary>
    IChangeToken? GetConfigurationChangeToken();
}

/// <summary>Default <see cref="ISettingPins"/>.</summary>
public sealed class SettingPins(
    ISettingDefinitionCatalog catalog, SettingsOptions options, IConfiguration? configuration = null) : ISettingPins {
    /// <inheritdoc />
    public bool IsPinned(SettingDefinition definition, SettingsScope scope) {
        catalog.Require(definition);
        return scope.Kind == SettingsScope.GlobalKind && GetPinnedText(definition) is not null;
    }

    /// <inheritdoc />
    public string? GetPinnedText(SettingDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);
        if (configuration is null || !definition.IsPinnable) return null;

        string? raw = null;
        if (configuration is IConfigurationRoot root) {
            foreach (var provider in root.Providers.Reverse()) {
                if (provider is ISettingsProjectionProvider) continue;

                if (provider.TryGet(definition.Key, out raw)) break;
            }
        }
        else {
            raw = configuration[definition.Key];
        }

        if (raw is null) return null;

        return !options.EmptyConfigurationValuesPin && string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <inheritdoc />
    public IChangeToken? GetConfigurationChangeToken() {
        return configuration?.GetReloadToken();
    }
}
