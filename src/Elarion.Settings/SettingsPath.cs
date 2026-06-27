namespace Elarion.Settings;

/// <summary>
/// Helpers for the hierarchical key convention. Keys are flat strings whose hierarchy is <i>virtual</i> —
/// expressed by a separator (<see cref="Separator"/>), the same way environment variables encode an
/// <c>IConfiguration</c> tree. The store treats keys as opaque; only adapters and prefix watching interpret
/// the hierarchy.
/// </summary>
public static class SettingsPath {
    /// <summary>The hierarchy separator, matching the <c>IConfiguration</c> convention.</summary>
    public const char Separator = ':';

    /// <summary>
    /// Whether <paramref name="key"/> falls under <paramref name="prefix"/>. A <see langword="null"/> or empty
    /// prefix matches every key. Otherwise the key matches when it equals the prefix exactly or is a strict
    /// descendant of it (so prefix <c>"a:b"</c> matches <c>"a:b"</c> and <c>"a:b:c"</c>, but not <c>"a:bc"</c>).
    /// </summary>
    public static bool IsUnderPrefix(string key, string? prefix) {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrEmpty(prefix)) {
            return true;
        }

        if (!key.StartsWith(prefix, StringComparison.Ordinal)) {
            return false;
        }

        return key.Length == prefix.Length || key[prefix.Length] == Separator;
    }
}
