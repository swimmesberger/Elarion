namespace Elarion.Settings.EntityFrameworkCore;

/// <summary>
/// The persisted row backing <see cref="ISettingsStore"/>. A <see cref="SettingsScope"/> is flattened into
/// the <see cref="Kind"/> + <see cref="Owner"/> columns: because a relational primary key cannot contain a
/// nullable column, a non-owned scope (such as <see cref="SettingsScope.Global"/>) stores an empty
/// <see cref="Owner"/> rather than <see langword="null"/>. The composite key is <c>(Kind, Owner, Key)</c>.
/// </summary>
public sealed class Setting {
    /// <summary>The scope discriminator (for example <c>"global"</c> or <c>"user"</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>The scope owner, or the empty string for a non-owned scope.</summary>
    public required string Owner { get; init; }

    /// <summary>The hierarchical setting key.</summary>
    public required string Key { get; init; }

    /// <summary>The stored value, or <see langword="null"/> for a present-but-null setting.</summary>
    public string? Value { get; set; }

    /// <summary>When the row was last written.</summary>
    public DateTimeOffset UpdatedOnUtc { get; set; }

    /// <summary>The optimistic-concurrency version; starts at 1 and increments on every write.</summary>
    public int Version { get; set; }
}
