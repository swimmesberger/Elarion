namespace Elarion.Settings;

/// <summary>
/// Re-protects stored secret values so they converge on the current protection: legacy plaintext becomes
/// protected, and a payload under a retired key or an older scheme is rewritten under the current one. Idempotent —
/// a second run over converged data changes nothing — and safe to run on several nodes at once.
/// </summary>
public interface ISettingReprotector {
    /// <summary>
    /// Re-protects every secret entry of <paramref name="scope"/> (optionally limited to a key subtree). Each
    /// rewrite is guarded by the entry's version, so a concurrent write wins and the entry is reported as skipped
    /// instead of being overwritten with stale data. An entry that cannot be decrypted is counted, not thrown.
    /// Entries that no registered secret definition owns are left alone.
    /// </summary>
    ValueTask<SettingReprotectionReport> ReprotectAsync(
        SettingsScope scope,
        string? keyPrefix = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a re-protection run. Counts only; never a value.</summary>
public sealed record SettingReprotectionReport {
    /// <summary>The number of secret entries examined.</summary>
    public required int Scanned { get; init; }

    /// <summary>The number of entries rewritten.</summary>
    public required int Reprotected { get; init; }

    /// <summary>The number of entries left as they were because they changed concurrently.</summary>
    public required int Skipped { get; init; }

    /// <summary>The number of entries that could not be decrypted (for example their key ring is gone).</summary>
    public required int Failed { get; init; }
}

/// <summary>Default <see cref="ISettingReprotector"/> over the store, the catalog and the registered protector.</summary>
internal sealed class SettingReprotector(
    ISettingsStore store,
    ISettingDefinitionCatalog catalog,
    ISettingValueProtector? protector = null) : ISettingReprotector {
    private readonly SettingValueCodec _codec = new(protector);

    /// <inheritdoc />
    public async ValueTask<SettingReprotectionReport> ReprotectAsync(
        SettingsScope scope,
        string? keyPrefix = null,
        CancellationToken cancellationToken = default) {
        if (scope.IsCurrentUserPlaceholder)
            throw new ArgumentException("Re-protection needs a concrete scope, not the current-user placeholder.",
                nameof(scope));

        var entries = await store.GetAllAsync(scope, cancellationToken).ConfigureAwait(false);
        int scanned = 0, reprotected = 0, skipped = 0, failed = 0;

        foreach (var entry in entries) {
            if (entry.Value is null || !SettingsPath.IsUnderPrefix(entry.Key, keyPrefix) ||
                !catalog.TryGet(entry.Key, out var definition) || !definition.IsSecret ||
                !definition.AllowsScope(scope.Kind))
                continue;

            scanned++;
            string json;
            bool requiresReprotection;
            try {
                (json, requiresReprotection) = _codec.Decode(scope, entry.Key, entry, true);
            }
            catch (SettingProtectionException) {
                failed++;
                continue;
            }

            if (!requiresReprotection) continue;

            var (value, protection) = _codec.Encode(definition, scope, json);
            var result = await store.SetAsync(scope, entry.Key, value, protection, entry.Version, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess) reprotected++;
            else skipped++;
        }

        return new SettingReprotectionReport {
            Scanned = scanned, Reprotected = reprotected, Skipped = skipped, Failed = failed
        };
    }
}
