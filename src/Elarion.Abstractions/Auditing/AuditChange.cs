namespace Elarion.Abstractions.Auditing;

/// <summary>How an audited entity was affected.</summary>
public enum AuditChangeKind {
    /// <summary>The entity was created.</summary>
    Added,
    /// <summary>One property of an existing entity changed value.</summary>
    Modified,
    /// <summary>The entity was deleted.</summary>
    Deleted,
}

/// <summary>
/// One field-level change attached to an <see cref="AuditRecord"/>: which property of which entity moved
/// from <see cref="OldValue"/> to <see cref="NewValue"/>. Produced automatically by the persistence layer's
/// change contributors for <see cref="AuditedAttribute">[Audited]</see> entities, or added by the handler via
/// <see cref="IAuditScope.AddChange"/> for paths automatic capture cannot see.
/// </summary>
/// <remarks>
/// Values are carried as invariant-formatted strings so records stay serialization- and provider-neutral.
/// For <see cref="AuditChangeKind.Added"/> the property is conventionally empty and <see cref="NewValue"/>
/// is <see langword="null"/> (the fact recorded is the creation itself); <see cref="AuditChangeKind.Deleted"/>
/// mirrors that with the deletion.
/// </remarks>
public sealed record AuditChange {
    /// <summary>The entity type name (CLR simple name by convention, e.g. <c>"Property"</c>).</summary>
    public required string Entity { get; init; }

    /// <summary>The entity's key, invariant-formatted. <see langword="null"/> when unknown at capture time.</summary>
    public string? EntityId { get; init; }

    /// <summary>The changed property name; empty for entity-level facts (creation/deletion).</summary>
    public string Property { get; init; } = "";

    /// <summary>The value before the change, invariant-formatted; <see langword="null"/> when not applicable.</summary>
    public string? OldValue { get; init; }

    /// <summary>The value after the change, invariant-formatted; <see langword="null"/> when not applicable.</summary>
    public string? NewValue { get; init; }

    /// <summary>How the entity was affected.</summary>
    public required AuditChangeKind Kind { get; init; }
}
