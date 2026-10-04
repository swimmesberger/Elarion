using Microsoft.Extensions.Primitives;

namespace Elarion.Settings;

/// <summary>
/// The consuming API over declared settings. Every read goes <b>through the effective-value resolver</b> (default
/// &lt; store &lt; configuration pin), so code never sees a value other than the one that takes effect. Typed access
/// serializes through the canonical <c>IElarionJsonSerialization</c> options (the app's source-generated contexts,
/// no reflection), and scope-aware access resolves the per-user scope from the ambient <c>ICurrentUser</c>.
/// </summary>
/// <remarks>
/// Settings are addressed by their <see cref="SettingDefinition{T}"/>, never by a string key, so an undeclared
/// setting cannot be named at compile time; a definition that was not registered, or one used in a scope it does
/// not allow, is a programming error and throws <see cref="InvalidOperationException"/>. Runtime conditions are
/// <c>Result</c> failures: a write to a pinned definition and a lost optimistic race return an
/// <see cref="AppError"/> whose data is a <see cref="SettingWriteFailure"/>.
/// <para>
/// Scope resolution: an omitted scope means <see cref="SettingsScope.Global"/>. Passing
/// <see cref="SettingsScope.CurrentUser"/> resolves the owner from <c>ICurrentUser</c> and <b>fails closed</b>
/// (throws) when there is no authenticated user. Pass <see cref="SettingsScope.User"/> to target a specific user.
/// </para>
/// </remarks>
public interface ISettingsManager {
    /// <summary>Reads the effective value: the pinned configuration value, else the stored value, else the default.</summary>
    /// <exception cref="SettingProtectionException">A stored secret cannot be unprotected.</exception>
    ValueTask<T> GetAsync<T>(
        SettingDefinition<T> definition,
        SettingsScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the value to the store (protected at rest for a secret definition). Refused with a
    /// <see cref="SettingWriteFailureReason.Pinned"/> failure while deployment configuration pins the definition.
    /// </summary>
    ValueTask<Result<SettingWrite>> SetAsync<T>(
        SettingDefinition<T> definition,
        T value,
        SettingsScope? scope = null,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the stored value so the setting reverts to its default. The success value is whether an entry was
    /// removed. Refused like <see cref="SetAsync{T}"/> while the definition is pinned.
    /// </summary>
    ValueTask<Result<bool>> ResetAsync(
        SettingDefinition definition,
        SettingsScope? scope = null,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes every registered definition that allows the scope (optionally under a key prefix): its effective
    /// source, whether it is pinned, and — except for secrets — its value. Intended for an admin UI.
    /// </summary>
    ValueTask<IReadOnlyList<SettingDescription>> DescribeAsync(
        SettingsScope? scope = null,
        string? keyPrefix = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a change token that fires when the definition's effective value may have changed: a store change in
    /// the scope, or — for a pinnable global definition — a configuration reload.
    /// </summary>
    IChangeToken Watch(SettingDefinition definition, SettingsScope? scope = null);

    /// <summary>Returns a change token that fires when settings change under the given prefix and scope.</summary>
    IChangeToken Watch(string? keyPrefix = null, SettingsScope? scope = null);
}
