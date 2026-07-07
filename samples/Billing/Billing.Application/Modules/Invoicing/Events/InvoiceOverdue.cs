using Elarion.Abstractions.Messaging;

namespace Billing.Application.Modules.Invoicing.Events;

/// <summary>Announced when a sent invoice passes its due date and is flagged overdue by the nightly
/// <see cref="Jobs.OverdueReminderJob"/>. An <see cref="IIntegrationEvent"/> (after-commit plane): it is
/// recorded in the job's unit of work and delivered once the status change commits. It carries both the
/// invoice and its client so the per-client dunning actor can be addressed by <c>ClientId</c> (ADR-0046).</summary>
public sealed record InvoiceOverdue(Guid InvoiceId, Guid ClientId) : IIntegrationEvent;
