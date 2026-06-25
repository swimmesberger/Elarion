using Billing.Application.Domain;
using Billing.Application.Modules.Invoicing.Services;
using Billing.Application.Persistence;
using Elarion.Abstractions.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Modules.Invoicing.Jobs;

public sealed record SendInvoiceEmailPayload {
    public required Guid InvoiceId { get; init; }
}

/// <summary>A runtime job enqueued on demand with a typed payload. Loads the invoice, sends the email,
/// and marks it <c>Sent</c>. The no-op-if-not-<c>Draft</c> guard makes it idempotent, which matters
/// because deferred retry may run a fresh attempt after a transient failure.</summary>
[ScheduledJob("invoicing.sendInvoiceEmail")]
public sealed class SendInvoiceEmailJob(
    BillingDbContext db,
    IInvoiceEmailSender email,
    TimeProvider clock
) : IScheduledJob<SendInvoiceEmailPayload> {
    public async ValueTask ExecuteAsync(
        SendInvoiceEmailPayload payload, IScheduledJobContext context, CancellationToken ct) {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == payload.InvoiceId, ct);
        if (invoice is null || invoice.Status != InvoiceStatus.Draft) {
            return;   // already sent, or rolled back — nothing to do
        }

        var client = await db.Clients.FirstAsync(c => c.Id == invoice.ClientId, ct);
        await email.SendAsync(new InvoiceEmail {
            To = client.Email,
            InvoiceNumber = invoice.Number,
            AmountCents = invoice.AmountCents,
            Currency = invoice.Currency,
        }, ct);

        invoice.Status = InvoiceStatus.Sent;
        invoice.SentAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }
}
