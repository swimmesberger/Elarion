using Billing.Application.Persistence;
using Elarion.Abstractions;
using Elarion.Abstractions.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Modules.Invoicing.Events;

/// <summary>A second handler-form consumer of <see cref="InvoiceOverdue"/> — it stamps the invoice's overdue
/// notice time. It coexists with the <see cref="Actors.ClientDunningActor"/>'s relay on the <em>same</em>
/// event: both are handler-form consumers, and each is registered keyed by its own identity, so both run
/// (ADR-0046). Before that fix, two handler-form consumers of one event silently collided.</summary>
[ConsumeEvent]
public sealed class OverdueNotice(BillingDbContext db, TimeProvider clock) : IHandler<InvoiceOverdue> {
    public async ValueTask<Result> HandleAsync(InvoiceOverdue e, CancellationToken ct) {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == e.InvoiceId, ct);
        if (invoice is null) {
            return Result.Success();
        }

        invoice.OverdueNoticeSentAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
