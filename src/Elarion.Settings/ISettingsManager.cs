using Microsoft.Extensions.Primitives;

namespace Elarion.Settings;

/// <summary>
/// The native, AOT-clean consuming API over the settings sink. Typed access serializes through the canonical
/// <c>IElarionJsonSerialization</c> options (the app's source-generated contexts, no reflection), and scope-aware
/// reads resolve the per-user scope from the ambient <c>ICurrentUser</c>.
/// </summary>
/// <remarks>
/// Scope resolution: an omitted scope means <see cref="SettingsScope.Global"/>. Passing
/// <see cref="SettingsScope.CurrentUser"/> resolves the owner from <c>ICurrentUser</c> and <b>fails closed</b>
/// (throws) when there is no authenticated user — for example outside an HTTP request — matching the handler
/// cache's posture. Pass <see cref="SettingsScope.User"/> to target a specific user explicitly.
/// </remarks>
public interface ISettingsManager {
    /// <summary>
    /// Reads and deserializes a typed value (through the canonical serializer), returning
    /// <paramref name="fallback"/> when the key is absent (or stored as JSON null).
    /// </summary>
    ValueTask<T> GetAsync<T>(
        string key,
        T fallback,
        SettingsScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>Serializes <paramref name="value"/> (through the canonical serializer) and writes it.</summary>
    ValueTask<SettingWriteResult> SetAsync<T>(
        string key,
        T value,
        SettingsScope? scope = null,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a raw string value, or <see langword="null"/> when absent.</summary>
    ValueTask<string?> GetStringAsync(
        string key,
        SettingsScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a raw string value.</summary>
    ValueTask<SettingWriteResult> SetStringAsync(
        string key,
        string? value,
        SettingsScope? scope = null,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a value.</summary>
    ValueTask<bool> RemoveAsync(
        string key,
        SettingsScope? scope = null,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a change token that fires when settings change under the given prefix and scope.</summary>
    IChangeToken Watch(string? keyPrefix = null, SettingsScope? scope = null);
}
