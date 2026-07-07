using Elarion.Abstractions.Modules;

namespace Billing.Application.Modules.Core.Contracts;

/// <summary>The Core module's published <em>activity-log</em> capability. Activity history is queryable
/// domain data (an end-user feature reads it back), so recording it is a domain capability the Core module
/// owns and publishes as a <c>[ModuleContract]</c> — the sanctioned way another module depends on it (Clients
/// and Invoicing inject this to record). The implementation is Core-internal and persists an
/// <c>ActivityEntry</c> here; it could instead emit to an external sink behind its <em>own</em> port (e.g. an
/// <c>IGrafanaClient</c> whose adapter lives in <c>Billing.Infrastructure</c>) without changing this
/// contract — the contract is the domain seam, the port would be the mechanism seam.
///
/// <para>Not to be confused with the framework <b>audit trail</b> (<c>[Auditable]</c>): that records the
/// compliance fact "who performed which action, with what outcome" automatically in the pipeline, with no
/// contract to inject. This activity log is the app's own <em>domain</em> history — chosen here to
/// demonstrate <c>[ModuleContract]</c>. Use the framework audit trail for compliance; model an activity log
/// like this only when the history is a first-class feature the app queries.</para></summary>
[ModuleContract]
public interface IActivityLog {
    ValueTask RecordAsync(string action, string subjectId, CancellationToken ct = default);
}
