namespace Elarion.Settings;

/// <summary>Options for the settings subsystem, configured through <c>AddElarionSettings</c>.</summary>
public sealed class SettingsOptions {
    private readonly List<SettingDefinition> _definitions = [];

    /// <summary>The definitions registered so far, in registration order.</summary>
    public IReadOnlyList<SettingDefinition> Definitions => _definitions;

    /// <summary>
    /// Whether a configuration value that is empty or whitespace still pins a pinnable definition. The default is
    /// <see langword="false"/>: an empty value means "not set" (a blank environment variable or an unfilled
    /// template placeholder must not silently freeze a setting to the empty string). Set it when an empty
    /// string is a legitimate pinned value.
    /// </summary>
    public bool EmptyConfigurationValuesPin { get; set; }

    /// <summary>Registers definitions, typically <c>ElarionSettingDefinitions.All</c>.</summary>
    public SettingsOptions AddDefinitions(IEnumerable<SettingDefinition> definitions) {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions) AddDefinition(definition);

        return this;
    }

    /// <summary>Registers one definition. Registering the same instance twice is a no-op.</summary>
    public SettingsOptions AddDefinition(SettingDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_definitions.Contains(definition)) _definitions.Add(definition);

        return this;
    }
}
