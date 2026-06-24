namespace Billing.Application.Modules.Invoicing.Services;

/// <summary>The Invoicing module's email port. The concrete sender is a platform capability the host
/// supplies from infrastructure, keeping the flaky external dependency behind an interface the
/// resilience policy can wrap.</summary>
public interface IInvoiceEmailSender {
    Task SendAsync(InvoiceEmail email, CancellationToken ct);
}

public sealed record InvoiceEmail {
    public required string To { get; init; }
    public required string InvoiceNumber { get; init; }
    public required long AmountCents { get; init; }
    public required string Currency { get; init; }
}
