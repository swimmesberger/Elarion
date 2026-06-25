using System.Collections.Generic;
using Billing.Application.Domain;
using Billing.Application.Persistence;
using Elarion.Abstractions;
using Elarion.Abstractions.Caching;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Modules.Clients.Handlers;

/// <summary>Lists the current account's clients. Cached under the same <c>clients</c> tag that
/// <see cref="CreateClient"/> invalidates, so the list refreshes the moment a client is added.</summary>
[Cacheable("clients", DurationSeconds = 60)]
[RpcMethod("clients.list")]
public sealed class ListClients(BillingDbContext db, ICurrentUser user)
    : IHandler<ListClients.Query, Result<ListClients.Response>> {
    public sealed record Query : IQuery;
    public sealed record Item(Guid Id, string Number, string Name, string Email);
    public sealed record Response(IReadOnlyList<Item> Clients);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var items = await db.Clients
            .Where(c => c.OwnerId == user.UserId)
            .OrderBy(c => c.Number)
            .Select(c => new Item(c.Id, c.Number, c.Name, c.Email))
            .ToListAsync(ct);

        return new Response(items);
    }
}
