namespace Billing.Application.Abstractions;

/// <summary>A platform-capability port: records that an action happened, without prescribing <em>where</em>.
/// Because the dependency is intent-only — callers care only that the event is recorded, never how — it is
/// abstracted behind a port, and the adapter lives in <c>Billing.Infrastructure</c>.</summary>
/// <remarks>
/// <para>
/// The adapter here just logs, but it could write its <em>own</em> audit table and still be infrastructure:
/// the logic/infra line is domain-data vs. mechanism-state, not database vs. no-database. Audit rows the app
/// never queries as domain are mechanism state the mechanism owns — exactly like the EF Core outbox owns its
/// <c>OutboxMessage</c> table. It would become application data only if the app grew an audit <em>feature</em>
/// that queried that history.
/// </para>
/// <para>
/// It lives <strong>outside</strong> every <c>[AppModule]</c>, so any module may depend on it freely: the
/// module boundary analyzer (ELMOD002) treats everything outside a module as shareable. A cross-module
/// capability like this is a <em>port</em>, never a <c>[ModuleContract]</c> — contracts are the rare,
/// justified surface for genuine cross-module <em>domain</em> calls, not for shared infrastructure.
/// </para>
/// </remarks>
public interface IAuditTrail {
    void Record(string action, string subjectId);
}
