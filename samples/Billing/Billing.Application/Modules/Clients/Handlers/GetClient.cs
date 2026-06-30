using Billing.Application.Domain;
using Billing.Application.Persistence;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Caching;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Modules.Clients.Handlers;

/// <summary>Reads a client by id. <c>[RequirePermission]</c> gates the call before it runs: the generated
/// authorization decorator denies a caller without the <c>clients.read</c> permission with a 403 — the
/// same check under JSON-RPC, MCP, and HTTP. An <see cref="IQuery"/>, so the transaction decorator's
/// <c>AppliesTo</c> predicate excludes it; <c>[Cacheable]</c> caches per-user (the default scope), and the
/// query is scoped to <c>OwnerId == user.UserId</c> so one account never sees another's data.</summary>
[Handler("clients.get")]
[RequirePermission("clients", Verbs.Read)]
[Cacheable("clients", DurationSeconds = 120)]
public sealed class GetClient(BillingDbContext db, ICurrentUser user)
    : IHandler<GetClient.Query, Result<GetClient.Response>> {
    public sealed record Query(Guid Id) : IQuery;
    public sealed record Response(Guid Id, string Number, string Name, string Email);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var client = await db.Clients
            .Where(c => c.OwnerId == user.UserId && c.Id == query.Id)
            .Select(c => new Response(c.Id, c.Number, c.Name, c.Email))
            .FirstOrDefaultAsync(ct);

        return client is null
            ? AppError.NotFound($"Client {query.Id} was not found.")
            : client;
    }
}
