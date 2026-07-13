namespace Elarion.Abstractions.Auditing;

/// <summary>
/// The audit sink seam. The framework's audit decorators build one <see cref="AuditRecord"/> per audited
/// handler invocation and write it through this interface; the durable default is the EF Core sink in
/// <c>Elarion.Auditing.EntityFrameworkCore</c>. Replace the registration to ship records elsewhere
/// (a log pipeline, a SIEM) — the decorators and capture seams are unchanged (ADR-0045).
/// </summary>
public interface IAuditTrail {
    /// <summary>
    /// Records a success. When the caller's unit-of-work transaction is ambient the write must enlist in it —
    /// the record commits (or rolls back) atomically with the business change; with no ambient transaction
    /// the record is persisted immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record arrives as a <b>factory</b>, not a value: a persistence-backed sink must first flush the
    /// handler's pending writes (so its change-capture pipeline contributes their diffs to the audit scope) and
    /// only then materialize the record — invoking <paramref name="buildRecord"/> after capture completed. A
    /// sink with no capture pipeline (a log sink) simply invokes it immediately.
    /// </para>
    /// <para>
    /// The return value tells the pipeline whether the record is already durable
    /// (<see cref="AuditRecordDurability.Durable"/> — immediate persistence, no ambient transaction) or only
    /// <see cref="AuditRecordDurability.EnlistedInTransaction"/>. An enlisting sink must additionally promote
    /// the invocation's audit scope to recorded once its transaction commits (the EF sink flips it from a
    /// transaction interceptor), so that a commit-phase failure still surfaces as a detached failure record.
    /// </para>
    /// </remarks>
    ValueTask<AuditRecordDurability> RecordAsync(Func<AuditRecord> buildRecord, CancellationToken cancellationToken);

    /// <summary>
    /// Records a denial or failure on a detached path: the write must never enlist in an ambient transaction,
    /// because the caller's transaction (if any) is rolling back and the record has to survive it.
    /// </summary>
    ValueTask RecordDetachedAsync(AuditRecord record, CancellationToken cancellationToken);
}
