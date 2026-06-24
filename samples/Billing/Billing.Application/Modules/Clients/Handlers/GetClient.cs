using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Modules.Clients.Handlers;

/// <summary>Reads a client by id. Exposed over JSON-RPC as <c>clients.get</c>.</summary>
[RpcMethod("clients.get")]
public sealed class GetClient(IAppDbContext db)
    : IHandler<GetClient.Query, Result<GetClient.Response>> {
    public sealed record Query(Guid Id) : IQuery;
    public sealed record Response(Guid Id, string Name, string Email);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var client = await db.Clients
            .Where(c => c.Id == query.Id)
            .Select(c => new Response(c.Id, c.Name, c.Email))
            .FirstOrDefaultAsync(ct);

        return client is null
            ? AppError.NotFound($"Client {query.Id} was not found.")
            : client;
    }
}
