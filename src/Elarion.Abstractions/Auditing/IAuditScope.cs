namespace Elarion.Abstractions.Auditing;

/// <summary>
/// The ambient per-invocation audit accumulator. While an audited handler executes, three producers write
/// into one scope — the pipeline (actor/action/outcome), the persistence layer's change contributors
/// (field-level diffs for <see cref="AuditedAttribute">[Audited]</see> entities), and the handler itself —
/// and the framework drains it into a single <see cref="AuditRecord"/>.
/// </summary>
/// <remarks>
/// Handlers inject this to name the resource and to add what automatic capture cannot see: intent-level
/// details (display names, the rule that triggered), and changes made through change-tracker-bypassing
/// writes (<c>ExecuteUpdate</c>/raw SQL). Outside an audited invocation — or in a host without auditing
/// registered — <see cref="IsActive"/> is <see langword="false"/> and writes are ignored, so module code
/// stays host-independent without guarding every call.
/// </remarks>
public interface IAuditScope {
    /// <summary>Whether an audited handler invocation is currently being recorded.</summary>
    bool IsActive { get; }

    /// <summary>
    /// Names the resource this invocation acted on, with an optional parent/aggregate reference so the record
    /// surfaces on the parent's audit view (e.g. the fire extinguisher <em>and</em> its property).
    /// </summary>
    void SetResource(string type, string id, string? parentType = null, string? parentId = null);

    /// <summary>Appends one field-level change to the invocation's record.</summary>
    void AddChange(AuditChange change);

    /// <summary>Adds (or overwrites) one free-form, string-valued detail. Keep values free of secrets.</summary>
    void AddDetail(string name, string value);
}
