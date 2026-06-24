using Billing.Application.Modules.Invoicing.Services;
using Microsoft.Extensions.Logging;

namespace Billing.Infrastructure.Email;

/// <summary>The concrete email sender behind the Invoicing module's port. Deliberately thin — swap this
/// for MailKit / SES / SendGrid. The contract, and the <see cref="CancellationToken"/> the resilience
/// timeout depends on, stays the same.</summary>
public sealed class SmtpInvoiceEmailSender(ILogger<SmtpInvoiceEmailSender> logger)
    : IInvoiceEmailSender {
    public async Task SendAsync(InvoiceEmail email, CancellationToken ct) {
        logger.LogInformation("Sending invoice {Number} to {To}", email.InvoiceNumber, email.To);
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
    }
}
