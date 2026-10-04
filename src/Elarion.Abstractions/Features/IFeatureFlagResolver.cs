namespace Elarion.Abstractions.Features;

/// <summary>
/// The owner of one <b>code-defined</b> feature flag: the class that carries <see cref="FeatureFlagAttribute"/>
/// implements this contract and is the only thing that decides the flag's value. A resolver is resolved from the
/// DI scope (scoped by default), so it can inject a <c>DbContext</c>, a clock, or settings, but it receives the
/// subject it is asked about explicitly as a <see cref="FeatureEvaluationContext"/>.
/// </summary>
/// <remarks>
/// Backend-evaluated flags (<see cref="BackendFeatureFlagAttribute"/>) have no resolver class: the host's single
/// <see cref="IBackendFeatureFlagEvaluator"/> owns them. Either way exactly one owner exists per flag, so there is
/// no precedence order to reason about.
/// </remarks>
/// <example>
/// <code>
/// [FeatureFlag("beta-reports", Description = "Early access to the reports redesign.", ExposeToClient = true)]
/// public sealed class BetaReportsFlag(IBetaRoster roster) : IFeatureFlagResolver {
///     public async ValueTask&lt;bool&gt; IsEnabledAsync(FeatureEvaluationContext context, CancellationToken ct) =&gt;
///         context.UserId is { } id &amp;&amp; await roster.ContainsAsync(id, ct);
/// }
/// </code>
/// </example>
public interface IFeatureFlagResolver {
    /// <summary>Returns whether the flag is enabled for <paramref name="context"/>.</summary>
    ValueTask<bool> IsEnabledAsync(FeatureEvaluationContext context, CancellationToken ct);

    /// <summary>
    /// Returns the variant allocated to <paramref name="context"/> for a multivariate flag, or
    /// <see langword="null"/> when the flag has no variant for it. The default is no variant, so a plain
    /// on/off flag implements only <see cref="IsEnabledAsync"/>.
    /// </summary>
    ValueTask<string?> GetVariantAsync(FeatureEvaluationContext context, CancellationToken ct) {
        return default;
    }
}
