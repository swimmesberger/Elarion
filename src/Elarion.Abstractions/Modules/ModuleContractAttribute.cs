namespace Elarion.Abstractions.Modules;

/// <summary>
/// Marks an interface (or class) as a module's <em>published cross-module contract</em> — the
/// stable, intentional surface other modules are allowed to depend on.
/// </summary>
/// <remarks>
/// <para>
/// In a modular monolith, modules collaborate synchronously by depending on a contract, not on
/// each other's internals — the in-process analog of a gRPC service contract. A module exposes
/// such a contract as an interface marked with this attribute and keeps the implementation
/// internal. Other modules inject the contract; the owning module registers the implementation
/// (commonly a thin adapter that maps to, and forwards to, the module's handlers).
/// </para>
/// <para>
/// The module-boundary analyzer (<c>ELMOD002</c>) keys off this attribute: referencing another
/// module's internal <c>[Service]</c>, handler, or entity type is reported, while a type marked
/// <see cref="ModuleContractAttribute"/> is an allowed cross-module reference.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Module A — the published contract (stable, public):
/// [ModuleContract]
/// public interface ICustomerLookup {
///     ValueTask&lt;Result&lt;Customer&gt;&gt; GetAsync(CustomerId id, CancellationToken ct = default);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ModuleContractAttribute : Attribute;
