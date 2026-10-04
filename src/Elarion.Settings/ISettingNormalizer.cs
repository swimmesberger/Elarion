using System.Globalization;
using Elarion.Abstractions.Serialization;

namespace Elarion.Settings;

/// <summary>
/// Converges legacy stored values on the canonical JSON form, so an application that adopted definitions over an
/// existing settings table runs one supported call instead of writing its own conversion pass. Idempotent — a
/// second run over converged data changes nothing — and safe to run on several nodes at once.
/// </summary>
/// <remarks>
/// <para>
/// Stored values are canonical JSON text. Rows written before the definition existed commonly hold the raw text
/// (<c>smtp.example.com</c>, <c>30</c>, <c>True</c>, <c>Red</c>) instead of its JSON form. For every definition the
/// normalizer first checks whether the stored text already is valid JSON for the definition's type
/// (<see cref="SettingNormalizationOutcome.AlreadyCanonical"/>, left untouched). Otherwise it attempts one
/// well-defined legacy coercion and rewrites the row (<see cref="SettingNormalizationOutcome.Rewritten"/>):
/// </para>
/// <list type="bullet">
/// <item><description><see cref="string"/>: the raw text becomes a JSON string, verbatim.</description></item>
/// <item><description><see cref="bool"/>: <c>true</c>/<c>false</c>, case-insensitive, surrounding whitespace ignored.</description></item>
/// <item><description>Integer types, <see cref="decimal"/>, <see cref="double"/>, <see cref="float"/>: the trimmed
/// text parsed with the invariant culture (<c>" 30 "</c>, <c>"030"</c>, <c>"1.5"</c>); out-of-range text is not
/// coerced.</description></item>
/// <item><description>Enums: a member name, case-insensitively (or the numeric value of a defined member).</description></item>
/// <item><description>Other scalar types serialized as JSON strings (<see cref="Guid"/>, <see cref="TimeSpan"/>,
/// <see cref="DateTimeOffset"/>, <see cref="Uri"/>, ...): the trimmed raw text as a JSON string, when the type
/// accepts it.</description></item>
/// <item><description>Records and collections: never coerced. Only text that already is valid JSON for the type is
/// accepted; anything else is <see cref="SettingNormalizationOutcome.Unreadable"/>.</description></item>
/// </list>
/// <para>
/// A raw value that is itself valid JSON for the type is indistinguishable from canonical form and left alone, so
/// <c>42</c> stored for an <see cref="int"/> is fine and stored for a <see cref="string"/> definition is rewritten
/// to <c>"42"</c> (reads deliberately do not guess).
/// </para>
/// <para>
/// <b>Secrets.</b> A secret row is decrypted, its plaintext representation normalized, and written back in the
/// protection state it already had: a row protected with the registered protector's scheme is protected again with
/// that same scheme; a legacy plaintext row stays plaintext. Normalization never changes a row's protection scheme
/// behind the application's back — converting plaintext secrets to protected form is the explicit job of
/// <see cref="ISettingReprotector"/>. A row whose protection cannot be undone (other scheme, missing key, no
/// protector) is <see cref="SettingNormalizationOutcome.Unreadable"/> and is never removed, even with
/// <see cref="SettingNormalizationOptions.RemoveUnrecoverable"/>: that is an environment problem, not legacy data.
/// </para>
/// </remarks>
public interface ISettingNormalizer {
    /// <summary>
    /// Normalizes the entries of <paramref name="scope"/> (optionally limited to a key subtree by
    /// <paramref name="options"/>). Each write is guarded by the entry's version, so a concurrent write wins and
    /// the entry is reported as <see cref="SettingNormalizationOutcome.Skipped"/>. Entries no registered definition
    /// owns (or whose definition does not allow the scope) are left alone and not reported.
    /// </summary>
    ValueTask<SettingNormalizationReport> NormalizeAsync(
        SettingsScope scope,
        SettingNormalizationOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Options of a normalization run.</summary>
public sealed record SettingNormalizationOptions {
    /// <summary>Limits the run to this key and its subtree (<see cref="SettingsPath"/> semantics), or all keys when <see langword="null"/>.</summary>
    public string? KeyPrefix { get; init; }

    /// <summary>
    /// Removes rows whose readable text cannot be coerced to the definition's type
    /// (<see cref="SettingNormalizationOutcome.Removed"/>) so the definition's default applies again, instead of
    /// leaving them <see cref="SettingNormalizationOutcome.Unreadable"/>. Rows whose protection cannot be undone
    /// are never removed. Defaults to <see langword="false"/>.
    /// </summary>
    public bool RemoveUnrecoverable { get; init; }
}

/// <summary>What a normalization run did to one entry.</summary>
public enum SettingNormalizationOutcome {
    /// <summary>The stored value was a legacy representation and was rewritten to canonical JSON.</summary>
    Rewritten,

    /// <summary>The stored value already was valid JSON for the definition's type; nothing was written.</summary>
    AlreadyCanonical,

    /// <summary>The value cannot be read or coerced; it was left as it is. See <see cref="SettingNormalizationEntry.Reason"/>.</summary>
    Unreadable,

    /// <summary>The unrecoverable row was removed (<see cref="SettingNormalizationOptions.RemoveUnrecoverable"/>).</summary>
    Removed,

    /// <summary>The entry changed concurrently, so it was left as it is; a later run picks it up.</summary>
    Skipped
}

/// <summary>The outcome for one key. Never carries a value.</summary>
/// <param name="Key">The setting key.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Reason">Why, for <see cref="SettingNormalizationOutcome.Unreadable"/> and <see cref="SettingNormalizationOutcome.Removed"/>; never contains the value.</param>
public sealed record SettingNormalizationEntry(string Key, SettingNormalizationOutcome Outcome, string? Reason = null);

/// <summary>The outcome of a normalization run: one entry per examined key. Keys and reasons only; never a value.</summary>
public sealed record SettingNormalizationReport {
    /// <summary>The per-key outcomes in store order.</summary>
    public required IReadOnlyList<SettingNormalizationEntry> Entries { get; init; }

    /// <summary>The number of entries examined.</summary>
    public int Scanned => Entries.Count;

    /// <summary>The number of entries rewritten to canonical JSON.</summary>
    public int Rewritten => Count(SettingNormalizationOutcome.Rewritten);

    /// <summary>The number of entries that already were canonical.</summary>
    public int AlreadyCanonical => Count(SettingNormalizationOutcome.AlreadyCanonical);

    /// <summary>The number of entries left unreadable.</summary>
    public int Unreadable => Count(SettingNormalizationOutcome.Unreadable);

    /// <summary>The number of unrecoverable entries removed.</summary>
    public int Removed => Count(SettingNormalizationOutcome.Removed);

    /// <summary>The number of entries left as they were because they changed concurrently.</summary>
    public int Skipped => Count(SettingNormalizationOutcome.Skipped);

    /// <summary>The entries with the given outcome, for logging which keys were changed or are unreadable.</summary>
    public IEnumerable<SettingNormalizationEntry> Where(SettingNormalizationOutcome outcome) {
        return Entries.Where(e => e.Outcome == outcome);
    }

    private int Count(SettingNormalizationOutcome outcome) {
        return Entries.Count(e => e.Outcome == outcome);
    }
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
        SettingNormalizationOptions? options = null,
        CancellationToken cancellationToken = default) {
        if (scope.IsCurrentUserPlaceholder)
            throw new ArgumentException("Normalization needs a concrete scope, not the current-user placeholder.",
                nameof(scope));

        options ??= new SettingNormalizationOptions();
        var entries = await store.GetAllAsync(scope, cancellationToken).ConfigureAwait(false);
        var results = new List<SettingNormalizationEntry>();

        foreach (var entry in entries) {
            if (entry.Value is null || !SettingsPath.IsUnderPrefix(entry.Key, options.KeyPrefix) ||
                !catalog.TryGet(entry.Key, out var definition) || !definition.AllowsScope(scope.Kind))
                continue;

            results.Add(await NormalizeEntryAsync(scope, entry, definition, options, cancellationToken)
                .ConfigureAwait(false));
        }

        return new SettingNormalizationReport { Entries = results };
    }

    private async ValueTask<SettingNormalizationEntry> NormalizeEntryAsync(
        SettingsScope scope,
        SettingEntry entry,
        SettingDefinition definition,
        SettingNormalizationOptions options,
        CancellationToken cancellationToken) {
        string plaintext;
        try {
            (plaintext, _) = _codec.Decode(scope, entry.Key, entry, definition.IsSecret);
        }
        catch (SettingProtectionException ex) {
            return new SettingNormalizationEntry(entry.Key, SettingNormalizationOutcome.Unreadable, ex.Message);
        }

        if (definition.TryValidateJson(plaintext, serialization, out var invalidReason))
            return new SettingNormalizationEntry(entry.Key, SettingNormalizationOutcome.AlreadyCanonical);

        if (TryCoerce(definition, plaintext, out var canonical)) {
            // Keep the row's protection state: protected stays protected with the scheme that decoded it.
            var (value, protection) = entry.Protection is null
                ? (canonical, null)
                : _codec.Protect(scope, entry.Key, canonical);
            var written = await store.SetAsync(scope, entry.Key, value, protection, entry.Version, cancellationToken)
                .ConfigureAwait(false);
            return new SettingNormalizationEntry(entry.Key,
                written.IsSuccess ? SettingNormalizationOutcome.Rewritten : SettingNormalizationOutcome.Skipped);
        }

        if (!options.RemoveUnrecoverable)
            return new SettingNormalizationEntry(entry.Key, SettingNormalizationOutcome.Unreadable, invalidReason);

        var removed = await store.RemoveAsync(scope, entry.Key, entry.Version, cancellationToken)
            .ConfigureAwait(false);
        return new SettingNormalizationEntry(entry.Key,
            removed ? SettingNormalizationOutcome.Removed : SettingNormalizationOutcome.Skipped, invalidReason);
    }

    private bool TryCoerce(SettingDefinition definition, string raw, out string canonical) {
        var type = Nullable.GetUnderlyingType(definition.ValueType) ?? definition.ValueType;
        if (type == typeof(string)) {
            canonical = SettingConfigurationText.QuoteString(raw);
            return true;
        }

        foreach (var candidate in Candidates(type, raw.Trim()))
            if (definition.TryCanonicalizeJson(candidate, serialization, out var json, out _) && json is not null) {
                canonical = json;
                return true;
            }

        canonical = "";
        return false;
    }

    private static IEnumerable<string> Candidates(Type type, string text) {
        if (text.Length == 0) yield break;

        if (type == typeof(bool)) {
            if (bool.TryParse(text, out var boolean)) yield return boolean ? "true" : "false";
            yield break;
        }

        if (type.IsEnum) {
            if (Enum.TryParse(type, text, ignoreCase: true, out var member) &&
                (Enum.IsDefined(type, member) || type.IsDefined(typeof(FlagsAttribute), false))) {
                yield return SettingConfigurationText.QuoteString(member.ToString()!);
                yield return ((IFormattable)Convert.ChangeType(member, Enum.GetUnderlyingType(type),
                    CultureInfo.InvariantCulture)).ToString(null, CultureInfo.InvariantCulture);
            }

            yield break;
        }

        switch (Type.GetTypeCode(type)) {
            case TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32
                or TypeCode.UInt32 or TypeCode.Int64:
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                    yield return integer.ToString(CultureInfo.InvariantCulture);
                yield break;
            case TypeCode.UInt64:
                if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned))
                    yield return unsigned.ToString(CultureInfo.InvariantCulture);
                yield break;
            case TypeCode.Decimal:
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    yield return number.ToString(CultureInfo.InvariantCulture);
                yield break;
            case TypeCode.Double or TypeCode.Single:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) &&
                    double.IsFinite(floating))
                    yield return floating.ToString("R", CultureInfo.InvariantCulture);
                yield break;
        }

        // Scalars serialized as JSON strings (Guid, TimeSpan, DateTimeOffset, Uri, ...) accept a quoted bare word;
        // records and collections reject it, so they stay unreadable.
        yield return SettingConfigurationText.QuoteString(text);
    }
}
