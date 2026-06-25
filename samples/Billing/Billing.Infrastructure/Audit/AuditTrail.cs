using Billing.Application.Abstractions;
using Elarion.Abstractions.Identity;
using Microsoft.Extensions.Logging;

namespace Billing.Infrastructure.Audit;

/// <summary>The concrete sink behind the application's <see cref="IAuditTrail"/> port — deliberately thin
/// (it logs). Swap it for an audit table or a SIEM without touching a module, exactly like the SMTP email
/// adapter. It injects <see cref="ICurrentUser"/>, a transport-neutral view of the caller, so application
/// code never touches <c>HttpContext</c>. Host-wired in <c>Program.cs</c>, not module-generated, because an
/// intent-only adapter is infrastructure the host owns.</summary>
public sealed class AuditTrail(ICurrentUser user, TimeProvider clock, ILogger<AuditTrail> logger)
    : IAuditTrail {
    public void Record(string action, string subjectId) =>
        logger.LogInformation("{User} {Action} {Subject} at {At}",
            user.UserId, action, subjectId, clock.GetUtcNow());
}
