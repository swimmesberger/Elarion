using System.Collections.Generic;
using Billing.Application.Domain;
using Billing.Application.Persistence;
using Elarion.Abstractions;
using Elarion.Abstractions.Caching;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Modules.Invoicing.Handlers;

/// <summary>Lists the current account's invoices through a cached query, invalidated by the
/// <c>invoices</c> tag whenever <see cref="CreateInvoice"/> succeeds.</summary>
[Cacheable("invoices", DurationSeconds = 30)]
[Handler("invoices.list")]
public sealed class ListInvoices(BillingDbContext db, ICurrentUser user)
    : IHandler<ListInvoices.Query, Result<ListInvoices.Response>> {
    public sealed record Query : IQuery;
    public sealed record Item(
        Guid Id, string Number, long AmountCents, string Currency, string Status, DateOnly DueDate);
    public sealed record Response(IReadOnlyList<Item> Invoices);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var items = await db.Invoices
            .Where(i => i.OwnerId == user.UserId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new Item(
                i.Id, i.Number, i.AmountCents, i.Currency, i.Status.ToString(), i.DueDate))
            .ToListAsync(ct);

        return new Response(items);
    }
}
