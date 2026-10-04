namespace Elarion.Abstractions.Features;

/// <summary>
/// Declares a <b>code-defined</b> feature flag and makes the annotated class its owner: the class must implement
/// <see cref="IFeatureFlagResolver"/> and is the only thing that evaluates the flag. Declaration is the single
/// source of truth for the flag — its name, description, client exposure, and owner — and the source generators
/// build the flag catalog, the DI registrations, the typed accessors, and the client-facing vocabulary from it.
/// </summary>
/// <remarks>
/// <para>
/// A flag must be declared by exactly one of this attribute or <see cref="BackendFeatureFlagAttribute"/>. Using a
/// name in <see cref="FeatureGateAttribute"/> or <see cref="FeatureVariantAttribute"/> that nothing declares is a
/// build error (<c>ELFLAG001</c>), declaring one name twice — in one assembly or across referenced assemblies — is
/// a build error (<c>ELFLAG002</c>), and putting this attribute on a class that is not an
/// <see cref="IFeatureFlagResolver"/> is a build error (<c>ELFLAG003</c>) because the flag would have no owner.
/// The declaration must sit under an <c>[AppModule]</c> namespace (<c>ELFLAG004</c>): the module's feature gate
/// decides whether the flag exists in a deployment.
/// </para>
/// <para>
/// A flag is a <b>variant flag</b> when its resolver returns a variant from
/// <see cref="IFeatureFlagResolver.GetVariantAsync"/>; <see cref="FeatureVariantAttribute"/> then selects service
/// implementations by it. Variants are declared by the services that bind them, not here.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [FeatureFlag("beta-reports", Description = "Early access to the reports redesign.", ExposeToClient = true)]
/// public sealed class BetaReportsFlag : IFeatureFlagResolver { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class FeatureFlagAttribute(string name) : Attribute {
    /// <summary>The flag name — the key used by <see cref="FeatureGateAttribute"/> and the client snapshot.</summary>
    public string Name { get; } = name;

    /// <summary>What the flag controls, shown in the catalog and the generated client vocabulary.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether the flag is evaluated and returned to the frontend by the session snapshot
    /// (<c>elarion.session</c>). Exposure is opt-in per flag, so an internal flag never reaches the wire. A flag
    /// of a disabled module is never exposed.
    /// </summary>
    public bool ExposeToClient { get; init; }
}
