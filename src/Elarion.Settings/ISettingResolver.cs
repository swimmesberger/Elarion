using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Elarion.Settings;

/// <summary>
/// The one effective-value resolver. It layers, lowest to highest, the definition's <b>default</b>, the
/// <b>store</b> value, and — only for definitions declared <c>Pinnable</c> in the global scope — a
/// <b>configuration</b> value that <i>pins</i> the definition. <see cref="ISettingsManager"/> reads through it, so
/// code never sees a value other than the one that takes effect; the <c>IConfiguration</c> provider in
/// <c>Elarion.Settings.Configuration</c> is a projection of it, not a second source of truth.
/// </summary>
/// <remarks>
/// A configuration value pins a definition when it is present and, unless
/// <see cref="SettingsOptions.EmptyConfigurationValuesPin"/> is set, neither empty nor whitespace. Only
/// non-projection configuration providers are consulted (see <see cref="ISettingsProjectionProvider"/>).
/// </remarks>
public interface ISettingResolver {
    /// <summary>Whether deployment configuration currently pins <paramref name="definition"/> in <paramref name="scope"/>. Never touches the store.</summary>
    bool IsPinned(SettingDefinition definition, SettingsScope scope);

    /// <summary>Resolves one definition in a concrete scope.</summary>
    /// <exception cref="SettingProtectionException">A stored secret cannot be unprotected.</exception>
    /// <exception cref="InvalidOperationException">The definition is not registered or does not allow the scope.</exception>
    ValueTask<ResolvedSetting> ResolveAsync(
        SettingDefinition definition, SettingsScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves every registered definition that allows <paramref name="scope"/> (optionally under a key prefix)
    /// with one store read. An unreadable secret is reported through <see cref="ResolvedSetting.IsUnreadable"/>
    /// instead of throwing, so one broken entry does not hide the rest.
    /// </summary>
    ValueTask<IReadOnlyList<ResolvedSetting>> ResolveAllAsync(
        SettingsScope scope, string? keyPrefix = null, CancellationToken cancellationToken = default);

    /// <summary>A token that fires when the deployment configuration reloads, or <see langword="null"/> without one.</summary>
    IChangeToken? GetConfigurationChangeToken();
}

/// <summary>Default <see cref="ISettingResolver"/>.</summary>
public sealed class SettingResolver : ISettingResolver {
    private readonly IConfiguration? _configuration;
    private readonly ISettingDefinitionCatalog _catalog;
    private readonly SettingValueCodec _codec;
    private readonly SettingsOptions _options;
    private readonly ISettingsStore _store;

    /// <summary>Creates the resolver.</summary>
    public SettingResolver(
        ISettingDefinitionCatalog catalog,
        ISettingsStore store,
        SettingsOptions options,
        ISettingValueProtector? protector = null,
        IConfiguration? configuration = null) {
        _catalog = catalog;
        _store = store;
        _options = options;
        _codec = new SettingValueCodec(protector);
        _configuration = configuration;
    }

    /// <inheritdoc />
    public bool IsPinned(SettingDefinition definition, SettingsScope scope) {
        _catalog.Require(definition);
        return scope.Kind == SettingsScope.GlobalKind && definition.IsPinnable &&
               ReadConfiguration(definition) is not null;
    }

    /// <inheritdoc />
    public async ValueTask<ResolvedSetting> ResolveAsync(
        SettingDefinition definition, SettingsScope scope, CancellationToken cancellationToken = default) {
        _catalog.Require(definition);
        EnsureScope(definition, scope);

        SettingEntry? entry = null;
        if (!IsPinned(definition, scope))
            entry = await _store.GetAsync(scope, definition.Key, cancellationToken).ConfigureAwait(false);

        return Resolve(definition, scope, entry, false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ResolvedSetting>> ResolveAllAsync(
        SettingsScope scope, string? keyPrefix = null, CancellationToken cancellationToken = default) {
        var entries = await _store.GetAllAsync(scope, cancellationToken).ConfigureAwait(false);
        var byKey = new Dictionary<string, SettingEntry>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries) byKey[entry.Key] = entry;

        var results = new List<ResolvedSetting>();
        foreach (var definition in _catalog.All) {
            if (!definition.AllowsScope(scope.Kind) || !SettingsPath.IsUnderPrefix(definition.Key, keyPrefix))
                continue;

            SettingEntry? entry = byKey.TryGetValue(definition.Key, out var found) ? found : null;
            results.Add(Resolve(definition, scope, entry, true));
        }

        return results;
    }

    /// <inheritdoc />
    public IChangeToken? GetConfigurationChangeToken() {
        return _configuration?.GetReloadToken();
    }

    private ResolvedSetting Resolve(SettingDefinition definition, SettingsScope scope, SettingEntry? entry,
        bool bulk) {
        if (scope.Kind == SettingsScope.GlobalKind && definition.IsPinnable &&
            ReadConfiguration(definition) is { } raw)
            return new ResolvedSetting(definition, scope, SettingSource.Configuration, true,
                SettingConfigurationText.ToJson(definition, raw), null);

        if (entry is not { Value: not null } stored)
            return new ResolvedSetting(definition, scope, SettingSource.Default, false, null, null);

        try {
            var (json, requiresReprotection) = _codec.Decode(scope, definition.Key, stored, definition.IsSecret);
            return new ResolvedSetting(definition, scope, SettingSource.Store, false, json, stored.Version,
                requiresReprotection);
        }
        catch (SettingProtectionException) when (bulk) {
            return new ResolvedSetting(definition, scope, SettingSource.Store, false, null, stored.Version,
                IsUnreadable: true);
        }
    }

    private static void EnsureScope(SettingDefinition definition, SettingsScope scope) {
        if (!definition.AllowsScope(scope.Kind))
            throw new InvalidOperationException(
                $"Setting '{definition.Key}' does not allow the '{scope.Kind}' scope " +
                $"(allowed: {string.Join(", ", definition.Scopes)}).");
    }

    private string? ReadConfiguration(SettingDefinition definition) {
        if (_configuration is null) return null;

        string? raw = null;
        if (_configuration is IConfigurationRoot root) {
            foreach (var provider in root.Providers.Reverse()) {
                if (provider is ISettingsProjectionProvider) continue;

                if (provider.TryGet(definition.Key, out raw)) break;
            }
        }
        else {
            raw = _configuration[definition.Key];
        }

        if (raw is null) return null;

        return !_options.EmptyConfigurationValuesPin && string.IsNullOrWhiteSpace(raw) ? null : raw;
    }
}
