namespace Elarion.Abstractions.Features;

/// <summary>
/// Declares a <b>backend-evaluated</b> feature flag: the host's <see cref="IBackendFeatureFlagEvaluator"/>
/// (Microsoft.FeatureManagement, OpenFeature) is its single owner. Place it on any class under the declaring
/// module's namespace — typically the <c>[AppModule]</c> type or a static class that gathers the module's flags.
/// </summary>
/// <remarks>
/// The declaration is what makes the name known to the compiler and the runtime catalog; the flag's value and
/// targeting rules stay in the external system. The same one-declaration rules apply as for
/// <see cref="FeatureFlagAttribute"/> (<c>ELFLAG001</c>, <c>ELFLAG002</c>, <c>ELFLAG004</c>). A host that declares
/// backend-evaluated flags must register a backend (<c>AddElarionFeatureManagement</c>/<c>AddElarionOpenFeature</c>);
/// without one the host fails at startup rather than silently treating every flag as off.
/// </remarks>
/// <example>
/// <code>
/// [AppModule("Billing")]
/// [BackendFeatureFlag("new-export", Description = "The streaming invoice export.")]
/// [BackendFeatureFlag("dashboard-v2", ExposeToClient = true)]
/// public static class BillingModule { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class BackendFeatureFlagAttribute(string name) : Attribute {
    /// <summary>The flag name — the key used by <see cref="FeatureGateAttribute"/>, the backend, and the client snapshot.</summary>
    public string Name { get; } = name;

    /// <summary>What the flag controls, shown in the catalog and the generated client vocabulary.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether the flag is evaluated and returned to the frontend by the session snapshot
    /// (<c>elarion.session</c>). Exposure is opt-in per flag. A flag of a disabled module is never exposed.
    /// </summary>
    public bool ExposeToClient { get; init; }
}
