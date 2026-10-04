namespace Elarion.Settings;

/// <summary>
/// A single stored setting: a hierarchical string key mapped to a string value within a
/// <see cref="SettingsScope"/>, with a monotonically increasing <see cref="Version"/> used for optimistic
/// concurrency. The value is opaque to the store — the settings resolver decides how to interpret it (canonical
/// JSON produced by the typed accessor, or an opaque protected payload when <see cref="Protection"/> is set).
/// </summary>
/// <param name="Key">The hierarchical key (for example <c>"app:smtp"</c>); see <see cref="SettingsPath"/>.</param>
/// <param name="Value">The stored value, or <see langword="null"/> for a present-but-null setting.</param>
/// <param name="Protection">
/// Store metadata naming the scheme the <paramref name="Value"/> is protected with (the
/// <see cref="ISettingValueProtector.Scheme"/> that wrote it), or <see langword="null"/> when the value is stored
/// as plain JSON. It is a column of the entry, not a prefix inside the value, so protected and plaintext rows are
/// distinguishable without parsing the value and the scheme can evolve independently.
/// </param>
/// <param name="UpdatedOnUtc">When the entry was last written.</param>
/// <param name="Version">The current version; starts at 1 and increments on every successful write.</param>
public readonly record struct SettingEntry(
    string Key,
    string? Value,
    string? Protection,
    DateTimeOffset UpdatedOnUtc,
    int Version);
