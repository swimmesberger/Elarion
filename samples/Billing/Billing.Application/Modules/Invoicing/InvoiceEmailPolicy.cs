using Elarion.Abstractions.Resilience;

namespace Billing.Application.Modules.Invoicing;

/// <summary>A named resilience policy. The generator fills this partial class with <c>Name</c> and
/// <c>Reference</c> members and emits the registration the host calls. Retries up to four times with
/// exponential backoff and jitter, capping each attempt at 30 seconds.</summary>
[ResiliencePolicy(
    "invoice-email",
    MaxRetryAttempts = 4,
    Delay = "10s",
    Backoff = ResilienceBackoffType.Exponential,
    MaxDelay = "5m",
    UseJitter = true,
    Timeout = "30s")]
public static partial class InvoiceEmailPolicy;
