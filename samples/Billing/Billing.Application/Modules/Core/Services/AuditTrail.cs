using Billing.Application.Domain;
using Billing.Application.Modules.Core.Contracts;
using Billing.Application.Persistence;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;

namespace Billing.Application.Modules.Core.Services;

/// <summary>Core-internal implementation of the <see cref="IAuditTrail"/> contract. It persists an
/// <see cref="AuditEntry"/> through the shared <see cref="BillingDbContext"/>, so the record commits in the
/// same transaction as the command that triggered it (the transaction decorator wraps both). It injects
/// <see cref="ICurrentUser"/> for the acting user. Registered against the contract via
/// <c>[Service(typeof(IAuditTrail))]</c> and kept <c>internal</c>, so other modules depend on the
/// published contract, never this class.</summary>
[Service(typeof(IAuditTrail))]
internal sealed class AuditTrail(BillingDbContext db, ICurrentUser user, TimeProvider clock) : IAuditTrail {
    public async ValueTask RecordAsync(string action, string subjectId, CancellationToken ct = default) {
        db.AuditEntries.Add(new AuditEntry {
            Id = Guid.NewGuid(),
            ActorId = user.UserId,
            Action = action,
            SubjectId = subjectId,
            At = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }
}
