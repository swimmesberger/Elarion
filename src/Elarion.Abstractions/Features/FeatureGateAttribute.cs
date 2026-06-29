namespace Elarion.Abstractions.Features;

/// <summary>
/// Gates a handler behind one or more feature flags. When any declared gate is not satisfied, the handler is
/// short-circuited before it runs and the call fails with <see cref="AppError.NotFound(string)"/> — a disabled
/// feature is indistinguishable from a missing one, hiding the roadmap rather than advertising it (the same
/// hide-it behavior as Microsoft's MVC <c>[FeatureGate]</c>, but as a transport-neutral handler gate that works
/// identically under JSON-RPC, MCP, and HTTP).
/// </summary>
/// <remarks>
/// <para>
/// The attribute is intentionally shaped like the familiar ASP.NET MVC <c>[FeatureGate]</c>: pass one or more
/// feature names, optionally choosing <see cref="FeatureRequirement.Any"/> over the default
/// <see cref="FeatureRequirement.All"/>, and optionally <see cref="Negate"/> to gate on a feature being
/// <i>disabled</i>. It is <c>AllowMultiple</c>, so stacking several <c>[FeatureGate]</c> attributes ANDs them.
/// </para>
/// <example>
/// <code>
/// [FeatureGate("new-billing")]                       // enabled when "new-billing" is on
/// [FeatureGate("a", "b")]                             // enabled when BOTH a and b are on
/// [FeatureGate(FeatureRequirement.Any, "a", "b")]     // enabled when EITHER a or b is on
/// [FeatureGate("legacy-export", Negate = true)]       // enabled when "legacy-export" is OFF
/// </code>
/// </example>
/// <para>
/// The enforcing <see cref="FeatureGateDecorator{TRequest, TResponse}"/> is attached automatically by the handler
/// source generator just inside the authorization gate, so a denied feature never reaches the handler, caching, or
/// the rest of the pipeline. A handler whose response cannot represent failure (no
/// <see cref="IResultFailureFactory{TSelf}"/>) is reported at build time.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class FeatureGateAttribute : Attribute {
    /// <summary>Gates on the given features, requiring all of them (<see cref="FeatureRequirement.All"/>).</summary>
    /// <param name="features">One or more feature flag names.</param>
    public FeatureGateAttribute(params string[] features)
        : this(FeatureRequirement.All, features) {
    }

    /// <summary>Gates on the given features, combined according to <paramref name="requirement"/>.</summary>
    /// <param name="requirement">Whether all or any of the features must be enabled.</param>
    /// <param name="features">One or more feature flag names.</param>
    public FeatureGateAttribute(FeatureRequirement requirement, params string[] features) {
        Requirement = requirement;
        Features = features ?? [];
    }

    /// <summary>The feature flag names this gate evaluates.</summary>
    public IReadOnlyList<string> Features { get; }

    /// <summary>How <see cref="Features"/> are combined. Defaults to <see cref="FeatureRequirement.All"/>.</summary>
    public FeatureRequirement Requirement { get; }

    /// <summary>
    /// When <c>true</c>, the gate is satisfied when the feature(s) are <i>disabled</i> rather than enabled —
    /// useful for fencing off a legacy path while a replacement rolls out. Defaults to <c>false</c>.
    /// </summary>
    public bool Negate { get; set; }
}
