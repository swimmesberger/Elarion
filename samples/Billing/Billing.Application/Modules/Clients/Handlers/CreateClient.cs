using System.ComponentModel;
using Billing.Application.Domain;
using Billing.Application.Modules.Clients.Services;
using Billing.Application.Modules.Core.Contracts;
using Billing.Application.Persistence;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Caching;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Modules.Clients.Handlers;

/// <summary>Creates a client. <c>[RequirePermission]</c> requires the <c>clients.write</c> permission before
/// the handler runs. Then it runs the default pipeline (logging → validation → transaction), scopes the row
/// to the current user, invalidates the clients cache on success, and is exposed over JSON-RPC and as an MCP
/// tool. The <c>[Description]</c> attributes flow through to the MCP tool surface.</summary>
[Handler("clients.create")]
[RequirePermission("clients", Verbs.Write)]
[CacheInvalidate("clients")]
[Description("Creates a new client for the current account.")]
public sealed class CreateClient(
    BillingDbContext db,
    ICurrentUser user,
    IClientNumberGenerator numbers,
    IAuditTrail audit,
    TimeProvider clock
) : IHandler<CreateClient.Command, Result<CreateClient.Response>> {
    public sealed record Command : ICommand {
        [Description("The client's display name.")]
        public required string Name { get; init; }

        [Description("The client's billing email address.")]
        public required string Email { get; init; }
    }

    public sealed record Response(Guid Id, string Number);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var exists = await db.Clients
            .AnyAsync(c => c.OwnerId == user.UserId && c.Email == command.Email, ct);
        if (exists) {
            return AppError.Conflict($"A client with email {command.Email} already exists.");
        }

        var client = new Client {
            Id = Guid.NewGuid(),
            OwnerId = user.UserId,
            Number = await numbers.NextAsync(user.UserId, ct),
            Name = command.Name,
            Email = command.Email,
            CreatedAt = clock.GetUtcNow(),
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("client.created", client.Id.ToString(), ct);

        return new Response(client.Id, client.Number);
    }
}
