namespace Elarion.Settings;

/// <summary>The layer an effective setting value came from.</summary>
public enum SettingSource {
    /// <summary>The definition's declared default (or <c>default(T)</c> when it declares none).</summary>
    Default,

    /// <summary>A value written at runtime to the settings store.</summary>
    Store,

    /// <summary>A deployment-configuration value that pins the definition (<c>IConfiguration</c>).</summary>
    Configuration
}

/// <summary>
/// The effective value of one definition in one scope, as produced by the resolver.
/// </summary>
/// <param name="Definition">The definition.</param>
/// <param name="Scope">The concrete scope that was resolved.</param>
/// <param name="Source">The layer the value came from.</param>
/// <param name="IsPinned">Whether deployment configuration pins the definition, so runtime writes are refused.</param>
/// <param name="ValueJson">
/// The effective value as canonical JSON text for <see cref="SettingSource.Store"/> and
/// <see cref="SettingSource.Configuration"/> (decrypted for secrets — handle accordingly), or
/// <see langword="null"/> for <see cref="SettingSource.Default"/>: the default is read from the definition.
/// </param>
/// <param name="Version">The store entry's version when the value came from the store, otherwise <see langword="null"/>.</param>
/// <param name="RequiresReprotection">Whether the stored secret should be re-protected (plaintext or a retired key).</param>
/// <param name="IsUnreadable">
/// Whether a stored secret could not be unprotected (only reported by bulk resolution; a single resolution throws
/// <see cref="SettingProtectionException"/>). The value then falls back to nothing and <see cref="ValueJson"/> is
/// <see langword="null"/>.
/// </param>
public readonly record struct ResolvedSetting(
    SettingDefinition Definition,
    SettingsScope Scope,
    SettingSource Source,
    bool IsPinned,
    string? ValueJson,
    int? Version,
    bool RequiresReprotection = false,
    bool IsUnreadable = false) {
    /// <summary>Whether an effective value exists (a stored/pinned value, or a declared default).</summary>
    public bool HasValue => !IsUnreadable && (Source != SettingSource.Default || Definition.HasDefault);
}

/// <summary>
/// Describes one definition and its effective state for an admin surface. Transport-neutral: a host maps it to
/// whatever wire it exposes. The value is never present for a secret.
/// </summary>
public sealed record SettingDescription {
    /// <summary>The hierarchical key.</summary>
    public required string Key { get; init; }

    /// <summary>The CLR type of the value.</summary>
    public required Type ValueType { get; init; }

    /// <summary>The scope that was described.</summary>
    public required SettingsScope Scope { get; init; }

    /// <summary>The scope kinds the definition allows.</summary>
    public required IReadOnlyList<string> AllowedScopes { get; init; }

    /// <summary>Whether the value is a secret (protected at rest, never shown).</summary>
    public required bool IsSecret { get; init; }

    /// <summary>Whether deployment configuration may pin the definition.</summary>
    public required bool IsPinnable { get; init; }

    /// <summary>Whether deployment configuration currently pins the definition (runtime writes are refused).</summary>
    public required bool IsPinned { get; init; }

    /// <summary>Whether an effective value exists.</summary>
    public required bool HasValue { get; init; }

    /// <summary>The layer the effective value comes from.</summary>
    public required SettingSource Source { get; init; }

    /// <summary>
    /// The effective value as canonical JSON text — <see langword="null"/> for a secret (always), when there is no
    /// value, or when a stored secret is unreadable.
    /// </summary>
    public string? ValueJson { get; init; }

    /// <summary>The store entry's version, usable as <c>expectedVersion</c> for an optimistic write.</summary>
    public int? Version { get; init; }

    /// <summary>Whether a stored secret could not be unprotected (for example its key ring is gone).</summary>
    public bool IsUnreadable { get; init; }

    /// <summary>The definition's description, if declared.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// The stable error codes of a refused settings write (ADR-0080). A handler that returns the failure of
/// <see cref="ISettingsManager.SetAsync{T}"/> or <see cref="ISettingsManager.ResetAsync"/> declares the codes it can
/// surface with <see cref="ProducesErrorAttribute"/>, typically
/// <c>[ProducesError(SettingErrorCodes.Pinned, ErrorKind.BusinessRule, typeof(SettingWriteFailure))]</c>.
/// </summary>
public static class SettingErrorCodes {
    /// <summary>Deployment configuration pins the definition, so a stored value would be ignored.</summary>
    public const string Pinned = "settings.pinned";

    /// <summary>The expected version did not match the stored version (a lost optimistic race).</summary>
    public const string ConcurrencyConflict = "settings.concurrency_conflict";
}

/// <summary>The data carried by the <see cref="AppError"/> of a refused settings write; the error code says why.</summary>
/// <param name="Key">The setting key.</param>
public sealed record SettingWriteFailure(string Key);

/// <summary>The success value of a settings write.</summary>
/// <param name="Version">The new version of the store entry.</param>
public readonly record struct SettingWrite(int Version);
