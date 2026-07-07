using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Billing.Application.Domain;
using Billing.Application.Modules.Clients.Services;
using Billing.Application.Persistence;
using Elarion.Abstractions;
using Elarion.Abstractions.Auditing;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Caching;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Modules.Clients.Handlers;

/// <summary>Creates a client. <c>[RequirePermission]</c> requires the <c>clients.write</c> permission before
/// the handler runs. The DataAnnotations on <see cref="Command"/> auto-attach the framework validation
/// decorator (ADR-0027) ahead of the default pipeline (logging → transaction), so a bad request never opens a
/// transaction. The handler scopes the row to the current user, invalidates the clients cache on success, and
/// is exposed over JSON-RPC and as an MCP tool. The <c>[Description]</c> attributes flow through to the MCP
/// tool surface.</summary>
[Handler("clients.create")]
[HttpEndpoint("clients")]
[RequirePermission("clients", Verbs.Write)]
[CacheInvalidate("clients")]
[Auditable(Resource = "client")]   // framework audit trail: one compliance record per invocation (ADR-0045)
[Description("Creates a new client for the current account.")]
public sealed class CreateClient(
    BillingDbContext db,
    ICurrentUser user,
    IClientNumberGenerator numbers,
    IAuditScope audit,
    TimeProvider clock
) : IHandler<CreateClient.Command, Result<CreateClient.Response>> {
    /// <summary>Wire-shape constraints are declarative DataAnnotations (ADR-0027): one source feeds the
    /// auto-attached validation decorator, <c>rpc-schema.json</c>, the OpenAPI document, and the generated
    /// Zod client. Requiredness comes from <c>required</c>/non-nullability — no <c>[Required]</c>.</summary>
    public sealed record Command : ICommand {
        [Description("The client's display name.")]
        [StringLength(200, MinimumLength = 1)]
        public required string Name { get; init; }

        [Description("The client's billing email address.")]
        [EmailAddress]
        [StringLength(320)]
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
            Id = Guid.CreateVersion7(),
            OwnerId = user.UserId,
            Number = await numbers.NextAsync(user.UserId, ct),
            Name = command.Name,
            Email = command.Email,
            CreatedAt = clock.GetUtcNow(),
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync(ct);

        // The framework audit trail captured this whole invocation automatically ([Auditable] above);
        // SetResource just pins WHICH client, so the compliance record is queryable by resource. The record
        // commits atomically with the transaction, and [Audited] on Client adds the created row's snapshot.
        audit.SetResource("client", client.Id.ToString());

        return new Response(client.Id, client.Number);
    }
}
