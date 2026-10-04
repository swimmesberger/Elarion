namespace Elarion.Settings;

/// <summary>
/// The registered setting definitions: the single declaration the resolver, the manager, the describe API, and the
/// configuration projection all work from. Settings that are not declared here cannot be read or written.
/// </summary>
public interface ISettingDefinitionCatalog {
    /// <summary>Every registered definition, ordered by key.</summary>
    IReadOnlyList<SettingDefinition> All { get; }

    /// <summary>Finds a definition by key (case-insensitive, matching <c>IConfiguration</c> keys).</summary>
    bool TryGet(string key, out SettingDefinition definition);

    /// <summary>
    /// Verifies that <paramref name="definition"/> is registered in this catalog and returns it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The definition is not registered (an undeclared setting).</exception>
    SettingDefinition Require(SettingDefinition definition);
}

/// <summary>Default <see cref="ISettingDefinitionCatalog"/>, built once from <see cref="SettingsOptions"/>.</summary>
public sealed class SettingDefinitionCatalog : ISettingDefinitionCatalog {
    private readonly Dictionary<string, SettingDefinition> _byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the catalog and validates it.</summary>
    /// <exception cref="InvalidOperationException">Two different definitions share a key.</exception>
    /// <exception cref="SettingProtectionException">
    /// A secret definition is registered but no <see cref="ISettingValueProtector"/> is — failing here, at the
    /// first resolution (and at host start through the settings startup check), keeps a secret from ever being
    /// stored or read unprotected.
    /// </exception>
    public SettingDefinitionCatalog(SettingsOptions options, ISettingValueProtector? protector = null) {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var definition in options.Definitions)
            if (!_byKey.TryAdd(definition.Key, definition))
                throw new InvalidOperationException(
                    $"Setting key '{definition.Key}' is declared by more than one definition " +
                    "(keys are compared case-insensitively).");

        All = [.. _byKey.Values.OrderBy(static d => d.Key, StringComparer.Ordinal)];

        if (protector is null && All.Any(static d => d.IsSecret))
            throw new SettingProtectionException(
                "Secret settings are declared (" +
                string.Join(", ", All.Where(static d => d.IsSecret).Select(static d => d.Key)) + ") but no " +
                nameof(ISettingValueProtector) + " is registered. Register one, for example " +
                "AddElarionSettingsDataProtection from Elarion.Settings.DataProtection.");
    }

    /// <inheritdoc />
    public IReadOnlyList<SettingDefinition> All { get; }

    /// <inheritdoc />
    public bool TryGet(string key, out SettingDefinition definition) {
        ArgumentNullException.ThrowIfNull(key);
        return _byKey.TryGetValue(key, out definition!);
    }

    /// <inheritdoc />
    public SettingDefinition Require(SettingDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);
        if (_byKey.TryGetValue(definition.Key, out var registered) && ReferenceEquals(registered, definition))
            return registered;

        throw new InvalidOperationException(
            $"Setting '{definition.Key}' is not registered. Declare it with [Setting] in a [SettingDefinitions] " +
            "class and register it through AddElarionSettings(o => o.AddDefinitions(...)).");
    }
}
