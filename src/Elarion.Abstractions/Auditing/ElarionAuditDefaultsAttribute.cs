namespace Elarion.Abstractions.Auditing;

/// <summary>
/// Opts an assembly or module into audit-by-default: every <b>command</b> handler in scope is audited as if
/// it carried <see cref="AuditableAttribute"/>, unless it opts out with <c>[Auditable(Enabled = false)]</c>.
/// Resolved most-specific-wins (the handler's module beats the assembly), mirroring
/// <c>[ElarionAuthorizationDefaults]</c>.
/// </summary>
/// <remarks>
/// Queries are never audited by defaults — read auditing is deliberate and per-handler
/// (<c>[Auditable]</c> on the query). The source generator reads this at compile time and attaches the
/// audit decorators to every in-scope command handler.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ElarionAuditDefaultsAttribute : Attribute;
