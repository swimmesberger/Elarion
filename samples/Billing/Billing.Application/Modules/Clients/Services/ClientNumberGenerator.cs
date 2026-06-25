using Billing.Application.Domain;
using Billing.Application.Persistence;
using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Modules.Clients.Services;

public interface IClientNumberGenerator {
    ValueTask<string> NextAsync(string ownerId, CancellationToken ct);
}

[Service(typeof(IClientNumberGenerator))]
public sealed class ClientNumberGenerator(BillingDbContext db) : IClientNumberGenerator {
    public async ValueTask<string> NextAsync(string ownerId, CancellationToken ct) {
        var count = await db.Clients.CountAsync(c => c.OwnerId == ownerId, ct);
        return $"C-{count + 1:D6}";
    }
}
