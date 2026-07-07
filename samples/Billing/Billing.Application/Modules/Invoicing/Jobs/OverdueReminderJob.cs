using Billing.Application.Domain;
using Billing.Application.Modules.Invoicing.Events;
using Billing.Application.Persistence;
using Elarion.Abstractions.Messaging;
using Elarion.Abstractions.Resilience;
using Elarion.Abstractions.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Billing.Application.Modules.Invoicing.Jobs;

/// <summary>A recurring job that flags sent-but-unpaid invoices as overdue every morning. Uses inline
/// resilience (<c>[Resilient]</c>) — short, idempotent work where one occurrence owns its retries —
/// and config placeholders so operators can retune the schedule without a redeploy. Each newly-overdue
/// invoice is announced as an <see cref="InvoiceOverdue"/> integration event so the per-client
/// <see cref="Actors.ClientDunningActor"/> can coordinate escalation.</summary>
public sealed class OverdueReminderJob(
    BillingDbContext db,
    IIntegrationEventBus integrationEvents,
    TimeProvider clock,
    ILogger<OverdueReminderJob> logger
) {
    [Resilient(InvoiceEmailPolicy.Name)]
    [ScheduledJob(
        "invoicing.overdueReminders",
        Cron = "${Invoicing:OverdueCron:-0 0 8 * * *}",
        TimeZone = "Europe/Vienna",
        Enabled = "${Modules:Invoicing:Enabled:-true}")]
    public async ValueTask RunAsync(CancellationToken ct) {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var overdue = await db.Invoices
            .Where(i => i.Status == InvoiceStatus.Sent && i.DueDate < today)
            .ToListAsync(ct);

        foreach (var invoice in overdue) {
            invoice.Status = InvoiceStatus.Overdue;
            // Recorded in this job's unit of work — delivered after the status change commits. The
            // per-client dunning actor consumes it via [ConsumeEvent] (ADR-0046).
            await integrationEvents.PublishAsync(new InvoiceOverdue(invoice.Id, invoice.ClientId), ct);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Flagged {Count} invoices as overdue", overdue.Count);
    }
}
