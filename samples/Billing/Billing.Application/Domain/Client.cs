using Elarion.Abstractions.Auditing;

namespace Billing.Application.Domain;

/// <summary>A billing client. Lives in the shared-kernel <c>Billing.Application.Domain</c> namespace
/// (under no <c>[AppModule]</c>), so every module may depend on it without tripping the module-boundary
/// analyzer (ELMOD002). The <c>OwnerId</c> scopes each row to the signed-in account. The
/// <c>[EntityConfiguration]</c> (<see cref="Persistence.ClientConfiguration"/>) is what drives its
/// <c>DbSet</c> and schema.
///
/// <para><c>[Audited]</c> opts the entity into the framework audit trail's automatic change capture
/// (ADR-0045): while an <c>[Auditable]</c> handler runs, creations/deletions and per-field old→new edits are
/// recorded onto the audit record. Capture is opt-in per entity (fail-closed), and <c>[AuditIgnore]</c>
/// excludes a column — here <c>Email</c>, so the address never lands in the audit log. (Creating a client
/// records the row's creation; the ignore matters once an update handler edits fields.)</para></summary>
[Audited]
public sealed class Client {
    public Guid Id { get; set; }
    public required string OwnerId { get; set; }
    public required string Number { get; set; }
    public required string Name { get; set; }

    [AuditIgnore]   // PII: kept out of the audit trail's change capture
    public required string Email { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
