using Elarion.Abstractions.Messaging;

namespace Billing.Application.Modules.Invoicing.Events;

/// <summary>Announced when an invoice is created. The <see cref="IIntegrationEvent"/> marker binds it to
/// the after-commit plane: published inside the command, delivered durably once the transaction commits.</summary>
public sealed record InvoiceCreated(Guid InvoiceId) : IIntegrationEvent;
