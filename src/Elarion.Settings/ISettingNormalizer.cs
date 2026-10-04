using Elarion.Abstractions.Serialization;

namespace Elarion.Settings;

/// <summary>
/// Converges legacy stored values on the canonical JSON form, so an application that adopted definitions over an
/// existing settings table runs one supported call instead of writing its own conversion pass. Idempotent — a
/// second run over converged data changes nothing — and safe to run on several nodes at once.
/// </summary>
/// <remarks>
/// Stored values are canonical JSON text. Rows written before the definition existed commonly hold the raw string
/// (<c>smtp.example.com</c>, <c>42</c>) instead of its JSON form (<c>"smtp.example.com"</c>). For a definition whose
/// value type is <see cref="string"/> a stored value that is not a JSON string is unambiguously such a legacy raw
/// value and is rewritten as that literal string. Other types are never guessed — there a malformed value stays
/// unreadable (see <see cref="ResolvedSetting.IsUnreadable"/>) and is reported, to be fixed by hand. A legacy raw
/// string that itself happens to be valid JSON text (for example it was stored with its own quotes) is
/// indistinguishable from canonical form and is left alone.
/// </remarks>
public interface ISettingNormalizer {
    /// <summary>
    /// Normalizes the entries of <paramref name="scope"/> (optionally limited to a key subtree). Each rewrite is
    /// guarded by the entry's version, so a concurrent write wins and the entry is reported as skipped. A secret is
    /// decrypted, normalized and protected again with the current protector. Entries no registered definition owns
    /// are left alone.
    /// </summary>
    ValueTask<SettingNormalizationReport> NormalizeAsync(
        SettingsScope scope,
        string? keyPrefix = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a normalization run. Counts and keys only; never a value.</summary>
public sealed record SettingNormalizationReport {
    /// <summary>The number of entries examined.</summary>
    public required int Scanned { get; init; }

    /// <summary>The number of entries rewritten to canonical JSON.</summary>
    public required int Normalized { get; init; }

    /// <summary>The number of entries left as they were because they changed concurrently.</summary>
    public required int Skipped { get; init; }

    /// <summary>The keys of entries that are unreadable and cannot be normalized automatically.</summary>
    public required IReadOnlyList<string> UnreadableKeys { get; init; }
}

/// <summary>Default <see cref="ISettingNormalizer"/> over the store, the catalog and the registered protector.</summary>
internal sealed class SettingNormalizer(
    ISettingsStore store,
    ISettingDefinitionCatalog catalog,
    IElarionJsonSerialization serialization,
    ISettingValueProtector? protector = null) : ISettingNormalizer {
    private readonly SettingValueCodec _codec = new(protector);

    /// <inheritdoc />
    public async ValueTask<SettingNormalizationReport> NormalizeAsync(
        SettingsScope scope,
        string? keyPrefix = null,
        CancellationToken cancellationToken = default) {
        if (scope.IsCurrentUserPlaceholder)
            throw new ArgumentException("Normalization needs a concrete scope, not the current-user placeholder.",
                nameof(scope));

        var entries = await store.GetAllAsync(scope, cancellationToken).ConfigureAwait(false);
        var unreadable = new List<string>();
        int scanned = 0, normalized = 0, skipped = 0;

        foreach (var entry in entries) {
            if (entry.Value is null || !SettingsPath.IsUnderPrefix(entry.Key, keyPrefix) ||
                !catalog.TryGet(entry.Key, out var definition) || !definition.AllowsScope(scope.Kind))
                continue;

            scanned++;
            string json;
            try {
                (json, _) = _codec.Decode(scope, entry.Key, entry, definition.IsSecret);
            }
            catch (SettingProtectionException) {
                unreadable.Add(entry.Key);
                continue;
            }

            if (definition.TryValidateJson(json, serialization, out _)) continue;

            if (!IsStringDefinition(definition)) {
                unreadable.Add(entry.Key);
                continue;
            }

            var (value, protection) = _codec.Encode(definition, scope, SettingConfigurationText.QuoteString(json));
            var result = await store.SetAsync(scope, entry.Key, value, protection, entry.Version, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess) normalized++;
            else skipped++;
        }

        return new SettingNormalizationReport {
            Scanned = scanned, Normalized = normalized, Skipped = skipped, UnreadableKeys = unreadable
        };
    }

    private static bool IsStringDefinition(SettingDefinition definition) {
        return (Nullable.GetUnderlyingType(definition.ValueType) ?? definition.ValueType) == typeof(string);
    }
}
