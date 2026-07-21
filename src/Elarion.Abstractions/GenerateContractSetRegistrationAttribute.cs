namespace Elarion.Abstractions;

/// <summary>
/// Declares that the annotated partial method composes <em>every implementation of one contract in
/// this assembly</em> into DI — an infrastructure <em>contract set</em> (protocol packet bindings,
/// codec catalogs, pipeline stages) rather than a module service. The generator fills in the method
/// body; the host authors, names, and places the declaration:
/// <code>
/// public static partial class PacketBindingRegistrations {
///     [GenerateContractSetRegistration(typeof(IPacketBinding))]
///     public static partial IServiceCollection AddPacketBindings(this IServiceCollection services);
/// }
///
/// // host composition root:
/// services.AddPacketBindings();
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Contract sets are the module-less counterpart to <see cref="ServiceAttribute"/>: where module
/// services are <em>pushed</em> by the gated module bootstrapper (and disappear with a disabled
/// module), a contract set is <em>pulled</em> by the host — no bootstrapper invokes the method and
/// no configuration gates it. The host calls it from its composition root, exactly once,
/// unconditionally; the registry consuming the set decides whether an invalid set is fatal.
/// </para>
/// <para>
/// The method must be a <c>static partial</c> extension method with signature
/// <c>static partial IServiceCollection Name(this IServiceCollection services)</c>. Discovery is
/// compilation-local by design: every non-abstract, non-generic class declared in the same assembly
/// that is assignable to the contract is registered via <c>TryAddEnumerable</c>, so calling the
/// method twice never duplicates the set. Implementations contributed by referenced assemblies are
/// registered explicitly by the host.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class GenerateContractSetRegistrationAttribute(Type contractType) : Attribute {
    /// <summary>
    /// The contract whose implementation set is composed. Must be an interface or an abstract
    /// class (open generics are not supported).
    /// </summary>
    public Type ContractType { get; } = contractType;

    /// <summary>
    /// The DI lifetime for every implementation in the set. Defaults to
    /// <see cref="ServiceScope.Singleton"/> — infrastructure seams are boot-composed.
    /// </summary>
    public ServiceScope Scope { get; init; } = ServiceScope.Singleton;
}
