using Billing.Application.Domain;
using Elarion.Abstractions.Resilience;
using Elarion.Abstractions.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Billing.Application.Modules.Invoicing.Jobs;

/// <summary>A recurring job that flags sent-but-unpaid invoices as overdue every morning. Uses inline
/// resilience (<c>[Resilient]</c>) — short, idempotent work where one occurrence owns its retries —
/// and config placeholders so operators can retune the schedule without a redeploy.</summary>
public sealed class OverdueReminderJob(
    IAppDbContext db,
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
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Flagged {Count} invoices as overdue", overdue.Count);
    }
}
