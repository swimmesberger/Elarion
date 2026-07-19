namespace Elarion.Settings;

/// <summary>
/// The settings sink: a swappable read/write store keyed by <see cref="SettingsScope"/> and a hierarchical
/// string key. The database backend (<c>Elarion.Settings.EntityFrameworkCore</c>) is one implementation; an
/// in-process store ships in this package, and Redis or other backends can implement the same contract.
/// Listening is a separate concern — see <see cref="ISettingsChangeSource"/>.
/// </summary>
public interface ISettingsStore {
    /// <summary>Reads a single value, or <see langword="null"/> if the key is not present in the scope.</summary>
    ValueTask<string?> GetAsync(SettingsScope scope, string key, CancellationToken cancellationToken = default);

    /// <summary>Reads every entry in the scope (used for snapshots and the future <c>IConfiguration</c> load).</summary>
    ValueTask<IReadOnlyList<SettingEntry>> GetAllAsync(SettingsScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a value. When <paramref name="expectedVersion"/> is supplied the write is applied
    /// only if it matches the stored version (<c>0</c> meaning "expected absent"); otherwise it is
    /// unconditional. Returns the new version on success or <see cref="SettingWriteResult.ConcurrencyConflict"/>.
    /// </summary>
    ValueTask<SettingWriteResult> SetAsync(
        SettingsScope scope,
        string key,
        string? value,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a value. When <paramref name="expectedVersion"/> is supplied the removal is applied only if it
    /// matches the stored version. Returns whether an entry was removed.
    /// </summary>
    ValueTask<bool> RemoveAsync(
        SettingsScope scope,
        string key,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default);
}
