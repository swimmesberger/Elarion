using Billing.Domain;
using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Modules.Clients.Handlers;

/// <summary>Creates a client. Exposed over JSON-RPC as <c>clients.create</c>; injects the generated
/// <see cref="IAppDbContext"/> and returns a <see cref="Result{T}"/>.</summary>
[RpcMethod("clients.create")]
public sealed class CreateClient(IAppDbContext db, TimeProvider clock)
    : IHandler<CreateClient.Command, Result<CreateClient.Response>> {
    public sealed record Command : ICommand {
        public required string Name { get; init; }
        public required string Email { get; init; }
    }

    public sealed record Response(Guid Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var exists = await db.Clients.AnyAsync(c => c.Email == command.Email, ct);
        if (exists)
            return AppError.Conflict($"A client with email {command.Email} already exists.");

        var client = new Client {
            Id = Guid.NewGuid(),
            Name = command.Name,
            Email = command.Email,
            CreatedAt = clock.GetUtcNow(),
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync(ct);

        return new Response(client.Id);
    }
}
