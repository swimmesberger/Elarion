using Billing.Application.Domain;
using Billing.Application.Modules.Core.Contracts;
using Billing.Application.Persistence;
using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Modules.Core.Services;

/// <summary>Core-internal implementation of the <see cref="IAccountStanding"/> contract. It sums the
/// customer's <em>outstanding</em> invoices (sent or overdue, not yet paid) through the shared
/// <see cref="BillingDbContext"/> and rejects a new invoice that would push the total past a credit limit.
/// Registered against the contract via <c>[Service(typeof(IAccountStanding))]</c> and kept <c>internal</c>,
/// so other modules depend on the published contract, never this class. The limit is a constant here for the
/// sample; a real app would source it per customer.</summary>
[Service(typeof(IAccountStanding))]
internal sealed class AccountStanding(BillingDbContext db) : IAccountStanding {
    private const long CreditLimitCents = 1_000_000; // €10,000 per customer, for the sample

    public async ValueTask<Result> EnsureCanInvoiceAsync(Guid clientId, long amountCents, CancellationToken ct = default) {
        var outstanding = await db.Invoices
            .Where(i => i.ClientId == clientId && (i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.Overdue))
            .SumAsync(i => i.AmountCents, ct);

        if (outstanding + amountCents > CreditLimitCents) {
            return AppError.BusinessRule(
                $"Credit limit exceeded: outstanding {outstanding} + {amountCents} would pass {CreditLimitCents} (minor units).");
        }

        return Result.Success();
    }
}
