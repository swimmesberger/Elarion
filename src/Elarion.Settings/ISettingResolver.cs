using Elarion.Abstractions.Serialization;

namespace Elarion.Settings;

/// <summary>
/// The one effective-value resolver. It layers, lowest to highest, the definition's <b>default</b>, the
/// <b>store</b> value, and — only for definitions declared <c>Pinnable</c> in the global scope — a
/// <b>configuration</b> value that <i>pins</i> the definition. <see cref="ISettingsManager"/> reads through it, so
/// code never sees a value other than the one that takes effect; the <c>IConfiguration</c> provider in
/// <c>Elarion.Settings.Configuration</c> is a projection of it, not a second source of truth.
/// </summary>
/// <remarks>
/// The pin check itself lives in <see cref="ISettingPins"/> (a singleton that never touches the store). Bulk
/// resolution isolates a bad stored row: an entry that cannot be unprotected or is not a valid value of its
/// definition's type is reported through <see cref="ResolvedSetting.IsUnreadable"/> and
/// <see cref="ResolvedSetting.UnreadableReason"/> instead of failing the whole call.
/// </remarks>
public interface ISettingResolver {
    /// <summary>Resolves one definition in a concrete scope.</summary>
    /// <exception cref="SettingProtectionException">A stored secret cannot be unprotected.</exception>
    /// <exception cref="InvalidOperationException">The definition is not registered or does not allow the scope.</exception>
    ValueTask<ResolvedSetting> ResolveAsync(
        SettingDefinition definition, SettingsScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves every registered definition that allows <paramref name="scope"/> (optionally under a key prefix)
    /// with one store read. An unreadable entry (an undecryptable secret, or a stored value that is not valid for
    /// the definition's type) is reported through <see cref="ResolvedSetting.IsUnreadable"/> instead of throwing,
    /// so one broken entry does not hide the rest.
    /// </summary>
    ValueTask<IReadOnlyList<ResolvedSetting>> ResolveAllAsync(
        SettingsScope scope, string? keyPrefix = null, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="ISettingResolver"/>.</summary>
internal sealed class SettingResolver : ISettingResolver {
    private readonly ISettingDefinitionCatalog _catalog;
    private readonly SettingValueCodec _codec;
    private readonly ISettingPins _pins;
    private readonly IElarionJsonSerialization _serialization;
    private readonly ISettingsStore _store;

    /// <summary>Creates the resolver.</summary>
    public SettingResolver(
        ISettingDefinitionCatalog catalog,
        ISettingsStore store,
        ISettingPins pins,
        IElarionJsonSerialization serialization,
        ISettingValueProtector? protector = null) {
        _catalog = catalog;
        _store = store;
        _pins = pins;
        _serialization = serialization;
        _codec = new SettingValueCodec(protector);
    }

    /// <inheritdoc />
    public async ValueTask<ResolvedSetting> ResolveAsync(
        SettingDefinition definition, SettingsScope scope, CancellationToken cancellationToken = default) {
        _catalog.Require(definition);
        EnsureScope(definition, scope);

        SettingEntry? entry = null;
        if (!_pins.IsPinned(definition, scope))
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

    private ResolvedSetting Resolve(SettingDefinition definition, SettingsScope scope, SettingEntry? entry,
        bool bulk) {
        if (scope.Kind == SettingsScope.GlobalKind && _pins.GetPinnedText(definition) is { } raw)
            return new ResolvedSetting(definition, scope, SettingSource.Configuration, true,
                SettingConfigurationText.ToJson(definition, raw), null);

        if (entry is not { Value: not null } stored)
            return new ResolvedSetting(definition, scope, SettingSource.Default, false, null, null);

        try {
            var (json, requiresReprotection) = _codec.Decode(scope, definition.Key, stored, definition.IsSecret);
            if (bulk && !definition.TryValidateJson(json, _serialization, out var reason))
                return Unreadable(definition, scope, stored, reason);

            return new ResolvedSetting(definition, scope, SettingSource.Store, false, json, stored.Version,
                requiresReprotection);
        }
        catch (SettingProtectionException ex) when (bulk) {
            return Unreadable(definition, scope, stored, ex.Message);
        }
    }

    private static ResolvedSetting Unreadable(
        SettingDefinition definition, SettingsScope scope, SettingEntry stored, string? reason) {
        return new ResolvedSetting(definition, scope, SettingSource.Store, false, null, stored.Version,
            IsUnreadable: true, UnreadableReason: reason);
    }

    private static void EnsureScope(SettingDefinition definition, SettingsScope scope) {
        if (!definition.AllowsScope(scope.Kind))
            throw new InvalidOperationException(
                $"Setting '{definition.Key}' does not allow the '{scope.Kind}' scope " +
                $"(allowed: {string.Join(", ", definition.Scopes)}).");
    }
}
