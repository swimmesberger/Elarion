namespace Billing.Application.Domain;

/// <summary>An audit record — who did what, to which subject, and when. It lives in the shared-kernel
/// <c>Billing.Application.Domain</c> namespace like every other entity because it is <em>queryable domain
/// data</em>: an audit-history feature reads it back. That is exactly why recording it is a Core
/// <c>[ModuleContract]</c> capability rather than an infrastructure sink — a write-only log would be
/// mechanism state (a port), but a record the application queries is domain data. <c>ActorId</c> is the
/// acting user and scopes each row to that account.</summary>
public sealed class AuditEntry {
    public Guid Id { get; set; }
    public required string ActorId { get; set; }
    public required string Action { get; set; }
    public required string SubjectId { get; set; }
    public DateTimeOffset At { get; set; }
}
