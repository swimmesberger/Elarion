namespace Elarion.Abstractions.Modules;

/// <summary>
/// Marks a partial interface to be filled with a typed, in-process API over the owning module's
/// handlers — one method per handler, dispatched typed-direct to <c>IHandler&lt;TRequest, TResponse&gt;</c>
/// (so the handler's full decorator pipeline runs) with no serialization.
/// </summary>
/// <remarks>
/// <para>
/// This is the in-process analog of a generated transport client, but it is <em>not</em> a transport:
/// it crosses no serialization boundary and is absent from the JSON-RPC/MCP schema. It is an ergonomic
/// convenience for a module's own code (most notably a <c>[ModuleContract]</c> implementation that maps
/// to/from the contract's DTOs). Because the generated methods expose the handlers' request/response
/// types, the facade is module-internal and must not be injected across module boundaries.
/// </para>
/// <para>
/// Membership mirrors <c>[GenerateDbSets]</c>/<c>[DbEntity]</c>:
/// </para>
/// <list type="bullet">
///   <item><description>No <see cref="Scopes"/> (default facade): every non-excluded handler in the
///     owning module (resolved by longest-prefix namespace match) is included.</description></item>
///   <item><description>One or more <see cref="Scopes"/>: only handlers tagged with an intersecting
///     scope via <see cref="ModuleApiAttribute"/> are included.</description></item>
/// </list>
/// <para>
/// The interface must be declared <c>partial</c> and at namespace scope. The generator emits the method
/// declarations, an internal forwarder implementation, and a DI registration wired into the module's
/// gated <c>ConfigureDefaultServices</c>. A method per handler is named after the handler type.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Default facade — every non-excluded handler in this module:
/// [GenerateModuleApi]
/// public partial interface ICustomerApi;
///
/// // Scoped facade — only handlers tagged [ModuleApi("Reporting")]:
/// [GenerateModuleApi("Reporting")]
/// public partial interface IReportingApi;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class GenerateModuleApiAttribute(params string[] scopes) : Attribute
{
    /// <summary>
    /// The scopes this facade selects. Empty means the module's default facade (every non-excluded handler).
    /// </summary>
    public IReadOnlyList<string> Scopes { get; } = scopes;
}
