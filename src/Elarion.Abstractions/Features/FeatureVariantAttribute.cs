namespace Elarion.Abstractions.Features;

/// <summary>
/// Marks a <c>[Service]</c> as a <i>variant implementation</i>, selected at run time by the named feature flag's
/// allocated variant. It is a <b>modifier on a service registration</b>: the class must also carry <c>[Service]</c>
/// (which declares the service, its contract(s), and its lifetime), and <c>[FeatureVariant]</c> only changes how
/// those contracts are resolved — variant-keyed instead of plain. The contract is therefore <b>not</b> repeated
/// here; it is whatever the <c>[Service]</c> registers under (its implemented interfaces, or explicit
/// <c>[Service(typeof(...))]</c> types). When a service registers under several contracts, each becomes
/// variant-resolved. Consumers inject the contract transparently and Elarion resolves the implementation allocated
/// to the current user, so the only variant-aware code is the implementation classes.
/// </summary>
/// <remarks>
/// <example>
/// <code>
/// [Service]
/// [FeatureVariant("ForecastAlgorithm")]                 // the default (no Variant)
/// public sealed class LinearForecast : IForecastAlgorithm { ... }
///
/// [Service]
/// [FeatureVariant("ForecastAlgorithm", Variant = "neural")]
/// public sealed class NeuralForecast : IForecastAlgorithm { ... }
/// </code>
/// </example>
/// The implementation with no <see cref="Variant"/> is the default, used when no variant is allocated. A handler
/// that injects the contract directly is resolved transparently: the generator registers the handler behind an
/// async-resolving proxy that <c>await</c>s the variant for the current user before building the handler, so the
/// contract may have synchronous members. To resolve a variant outside a handler's constructor (a singleton, or a
/// service reached transitively), inject <see cref="IVariantServiceProvider{TService}"/> instead. A
/// <c>[FeatureVariant]</c> without <c>[Service]</c> is reported (<c>ELVAR007</c>).
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class FeatureVariantAttribute(string feature) : Attribute {
    /// <summary>The feature flag whose allocated variant selects the implementation.</summary>
    public string Feature { get; } = feature;

    /// <summary>
    /// The variant name this implementation is selected for. When omitted, this implementation is the
    /// <i>default</i> (used when no variant is allocated to the current user).
    /// </summary>
    public string? Variant { get; set; }

    /// <summary>
    /// Marks this implementation as the default <i>in addition to</i> its <see cref="Variant"/> — a <b>named
    /// default</b>: it serves users allocated that variant name and every user with no (or an unknown)
    /// allocation. Useful when the flag backend names the control group explicitly (e.g.
    /// <c>Variant = "control", IsDefault = true</c>).
    /// </summary>
    public bool IsDefault { get; set; }
}
