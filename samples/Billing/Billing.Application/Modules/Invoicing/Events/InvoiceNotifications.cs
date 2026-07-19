using Billing.Application.Domain;
using Billing.Application.Persistence;
using Elarion.Abstractions;
using Elarion.Abstractions.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Billing.Application.Modules.Invoicing.Events;

/// <summary>An integration-event consumer whose request <em>is</em> the event. Runs on a fresh scope
/// after the invoice commits, so it reads its own data and its failures never touch the original
/// command. Delivery is at-least-once, so keep it idempotent.</summary>
[ConsumeEvent]
public sealed class InvoiceNotifications(
    BillingDbContext db,
    ILogger<InvoiceNotifications> logger
) : IHandler<InvoiceCreated> {
    public async ValueTask<Result> HandleAsync(InvoiceCreated e, CancellationToken ct) {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == e.InvoiceId, ct);
        if (invoice is null) return Result.Success();

        var client = await db.Clients.FirstAsync(c => c.Id == invoice.ClientId, ct);
        // A real consumer would push to a webhook, search index, or notification service.
        logger.LogInformation("Invoice {Number} created for {Email}", invoice.Number, client.Email);
        return Result.Success();
    }
}
