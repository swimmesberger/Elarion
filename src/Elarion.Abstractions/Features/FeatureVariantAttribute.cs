namespace Elarion.Abstractions.Features;

/// <summary>
/// Marks a <c>[Service]</c> as a <i>variant implementation</i> of <typeparamref name="TContract"/>, selected at run
/// time by the named feature flag's allocated variant. It is a <b>modifier on a service registration</b>, not a
/// registration of its own: the class must also carry <c>[Service]</c> (which declares the service and its
/// lifetime), and <c>[FeatureVariant]</c> only changes how the contract is resolved. Consumers inject
/// <typeparamref name="TContract"/> transparently and Elarion resolves the right implementation for the current
/// user, so the only variant-aware code is the implementation classes.
/// </summary>
/// <typeparam name="TContract">The service contract this class implements and is selected for.</typeparam>
/// <remarks>
/// <example>
/// <code>
/// [Service]
/// [FeatureVariant&lt;IForecastAlgorithm&gt;("ForecastAlgorithm")]                 // the default (no Variant)
/// public sealed class LinearForecast : IForecastAlgorithm { ... }
///
/// [Service]
/// [FeatureVariant&lt;IForecastAlgorithm&gt;("ForecastAlgorithm", Variant = "neural")]
/// public sealed class NeuralForecast : IForecastAlgorithm { ... }
/// </code>
/// </example>
/// The implementation with no <see cref="Variant"/> is the default, used when no variant is allocated. A handler
/// that injects <typeparamref name="TContract"/> directly is resolved transparently: the generator registers the
/// handler behind an async-resolving proxy that <c>await</c>s the variant for the current user before building the
/// handler, so <typeparamref name="TContract"/> may have synchronous members. To resolve a variant outside a
/// handler's constructor (a singleton, or a service reached transitively), inject
/// <see cref="IVariantServiceProvider{TService}"/> instead. The implementation's DI lifetime comes from its
/// <c>[Service]</c> (<c>Scope</c>); a <c>[FeatureVariant]</c> without <c>[Service]</c> is reported (<c>ELVAR007</c>).
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class FeatureVariantAttribute<TContract>(string feature) : Attribute
    where TContract : class {
    /// <summary>The feature flag whose allocated variant selects the implementation.</summary>
    public string Feature { get; } = feature;

    /// <summary>
    /// The variant name this implementation is selected for. When omitted, this implementation is the
    /// <i>default</i> (used when no variant is allocated to the current user).
    /// </summary>
    public string? Variant { get; set; }
}
