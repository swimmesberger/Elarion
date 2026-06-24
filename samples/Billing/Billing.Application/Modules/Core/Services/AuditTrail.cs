using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Microsoft.Extensions.Logging;

namespace Billing.Application.Modules.Core.Services;

public interface IAuditTrail {
    void Record(string action, string subjectId);
}

/// <summary><c>[Service(typeof(IAuditTrail))]</c> registers the implementation against the interface;
/// the generator picks it up into the Core module's generated <c>ConfigureDefaultServices</c>. It
/// injects <see cref="ICurrentUser"/> — a transport-neutral view of the caller, so application code
/// never touches <c>HttpContext</c>.</summary>
[Service(typeof(IAuditTrail))]
public sealed class AuditTrail(ICurrentUser user, TimeProvider clock, ILogger<AuditTrail> logger)
    : IAuditTrail {
    public void Record(string action, string subjectId) =>
        logger.LogInformation("{User} {Action} {Subject} at {At}",
            user.UserId, action, subjectId, clock.GetUtcNow());
}
