namespace Elarion.Abstractions.Auditing;

/// <summary>
/// Reported by <see cref="IAuditTrail.RecordAsync"/>: whether the success record is already durable when the
/// call returns, or merely enlisted in the caller's ambient transaction and durable only once that
/// transaction commits. The pipeline uses it to decide when "recorded" becomes true — an enlisted record
/// that never commits must not suppress the detached failure record (ADR-0045).
/// </summary>
public enum AuditRecordDurability {
    /// <summary>The record is durably persisted when <see cref="IAuditTrail.RecordAsync"/> returns.</summary>
    Durable,

    /// <summary>
    /// The record is enlisted in the caller's ambient transaction and becomes durable only when that
    /// transaction commits. A sink returning this value must promote the invocation's audit scope to
    /// recorded once the commit succeeds (the EF sink does so from a transaction interceptor); if the
    /// commit fails, the scope stays unrecorded and the outer decorator writes the detached failure record.
    /// </summary>
    EnlistedInTransaction
}
