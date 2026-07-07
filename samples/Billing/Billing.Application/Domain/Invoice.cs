using Elarion.Abstractions.Auditing;

namespace Billing.Application.Domain;

/// <summary>An invoice issued to a <see cref="Client"/>. Money is stored as integer minor units
/// (<c>AmountCents</c>) to avoid floating-point rounding. The <c>[EntityConfiguration]</c>
/// (<see cref="Persistence.InvoiceConfiguration"/>) drives its <c>DbSet</c> and schema. <c>[Audited]</c>
/// opts it into the framework audit trail's automatic change capture (ADR-0045), like <see cref="Client"/>.</summary>
[Audited]
public sealed class Invoice {
    public Guid Id { get; set; }
    public required string OwnerId { get; set; }
    public Guid ClientId { get; set; }
    public required string Number { get; set; }
    public long AmountCents { get; set; }
    public required string Currency { get; set; }
    public InvoiceStatus Status { get; set; }
    public DateOnly DueDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? OverdueNoticeSentAt { get; set; }
}

public enum InvoiceStatus { Draft, Sent, Paid, Overdue, Cancelled }
