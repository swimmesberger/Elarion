namespace Elarion.Abstractions.Auditing;

/// <summary>
/// Opts an entity type into automatic audit change capture: while an audited handler executes, the persistence
/// layer's change contributor records field-level <see cref="AuditChange"/>s (old/new values) for this entity
/// on every flush, attached to the invocation's <see cref="AuditRecord"/>.
/// </summary>
/// <remarks>
/// Capture is opt-in per entity on purpose (fail-closed): auto-capturing every column risks recording
/// sensitive values, and opt-in keeps framework tables (outbox, idempotency, the audit log itself) out of
/// capture. Exclude individual properties with <see cref="AuditIgnoreAttribute"/>. Writes that bypass the
/// change tracker (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>/raw SQL) are never captured automatically — the
/// handler records those via <see cref="IAuditScope.AddChange"/> (ADR-0045).
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AuditedAttribute : Attribute;
