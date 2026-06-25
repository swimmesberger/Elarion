using Elarion.Abstractions.Modules;

namespace Billing.Application.Modules.Core.Contracts;

/// <summary>The Core module's published audit capability. Audit history is queryable domain data (an
/// end-user feature reads it back), so recording it is a domain capability the Core module owns and
/// publishes as a <c>[ModuleContract]</c> — the sanctioned way another module depends on it (Clients and
/// Invoicing inject this to record). The implementation is Core-internal and persists an <c>AuditEntry</c>
/// here; it could instead emit to an external sink behind its <em>own</em> port (e.g. an
/// <c>IGrafanaClient</c> whose adapter lives in <c>Billing.Infrastructure</c>) without changing this
/// contract — the contract is the domain seam, the port would be the mechanism seam.</summary>
[ModuleContract]
public interface IAuditTrail {
    ValueTask RecordAsync(string action, string subjectId, CancellationToken ct = default);
}
