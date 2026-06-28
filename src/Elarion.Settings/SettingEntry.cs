namespace Elarion.Settings;

/// <summary>
/// A single stored setting: a hierarchical string key mapped to a string value within a
/// <see cref="SettingsScope"/>, with a monotonically increasing <see cref="Version"/> used for optimistic
/// concurrency. The value is opaque to the store — the consuming side decides how to interpret it (a scalar,
/// or a JSON document produced by the typed accessor).
/// </summary>
/// <param name="Key">The hierarchical key (for example <c>"app:smtp:host"</c>); see <see cref="SettingsPath"/>.</param>
/// <param name="Value">The stored value, or <see langword="null"/> for a present-but-null setting.</param>
/// <param name="UpdatedOnUtc">When the entry was last written.</param>
/// <param name="Version">The current version; starts at 1 and increments on every successful write.</param>
public readonly record struct SettingEntry(string Key, string? Value, DateTimeOffset UpdatedOnUtc, int Version);
