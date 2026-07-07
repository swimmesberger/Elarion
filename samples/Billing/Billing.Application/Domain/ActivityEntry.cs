namespace Billing.Application.Domain;

/// <summary>An <em>activity-log</em> entry — a user-facing "recent activity" record the application reads
/// back (who did what, to which subject, when). It lives in the shared-kernel
/// <c>Billing.Application.Domain</c> namespace like every other entity because it is <em>queryable domain
/// data</em>: a history feature lists it. That is exactly why recording it is a Core <c>[ModuleContract]</c>
/// capability rather than an infrastructure sink — a write-only log would be mechanism state (a port), but a
/// record the application queries is domain data. <c>ActorId</c> is the acting user and scopes each row to
/// that account.
///
/// <para>This is deliberately distinct from the framework <b>audit trail</b> (<c>[Auditable]</c> +
/// <c>Elarion.Auditing.EntityFrameworkCore</c>, wired in <c>Program.cs</c>): that is the compliance record —
/// who performed which action with what outcome, committed atomically with the transaction, denied attempts
/// included — which the app does not model as its own domain data. See the "audit trail" concept doc for the
/// split. The two coexist here on purpose, as the worked example of that distinction.</para></summary>
public sealed class ActivityEntry {
    public Guid Id { get; set; }
    public required string ActorId { get; set; }
    public required string Action { get; set; }
    public required string SubjectId { get; set; }
    public DateTimeOffset At { get; set; }
}
