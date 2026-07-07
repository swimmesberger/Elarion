using Elarion.Abstractions.Auditing;
using Microsoft.EntityFrameworkCore;

namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>
/// Contributes field-level <see cref="AuditChange"/>s to the active audit scope on every flush of an audited
/// invocation. Registrations are <b>additive</b> (resolved as <c>IEnumerable&lt;&gt;</c>, all invoked, the
/// <c>IHandlerContextEnricher</c> shape): the default change-tracker contributor runs alongside any specialists
/// a host adds — a semantic differ for a JSON document column, a temporal-table producer, a cross-cutting
/// annotator. Unregister the default (or don't call the registration that adds it) for action-records-only.
/// </summary>
/// <remarks>
/// Invocation order is deterministic (registration order) but must never carry semantics — every contributor
/// appends to the same bag. Overlap is resolved by composition (exclude an entity from the default's
/// <c>[Audited]</c> opt-in when a specialist covers it), never by a priority system. Implementations are
/// scoped, so they may keep state between <see cref="OnSavingChanges"/> and <see cref="OnSavedChanges"/> —
/// which is how store-generated keys, temporary at saving time, are patched after the flush.
/// </remarks>
public interface IAuditChangeContributor {
    /// <summary>
    /// Called before each flush of an audited invocation, with original values still intact on the change
    /// tracker. Append changes via <c>context.Scope.AddChange(…)</c>.
    /// </summary>
    void OnSavingChanges(AuditCaptureContext context);

    /// <summary>
    /// Called after the flush succeeded, when store-generated values are final. Default no-op.
    /// </summary>
    void OnSavedChanges(AuditCaptureContext context) {
    }
}

/// <summary>The per-flush capture context handed to every <see cref="IAuditChangeContributor"/>.</summary>
public sealed class AuditCaptureContext {
    /// <summary>The flushing context, exposing the change tracker.</summary>
    public required DbContext DbContext { get; init; }

    /// <summary>The active audit scope to append changes to.</summary>
    public required IAuditScope Scope { get; init; }
}
