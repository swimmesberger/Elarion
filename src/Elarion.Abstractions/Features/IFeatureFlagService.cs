namespace Elarion.Abstractions.Features;

/// <summary>
/// The one entry point for evaluating a declared feature flag. It looks the flag up in the
/// <see cref="IFeatureFlagCatalog"/> and dispatches to the flag's single owner — its
/// <see cref="IFeatureFlagResolver"/> or the host's <see cref="IBackendFeatureFlagEvaluator"/> — with an explicit
/// <see cref="FeatureEvaluationContext"/>. <c>[FeatureGate]</c>, the session snapshot, and application code all
/// go through here, so a flag has one answer per context.
/// </summary>
/// <remarks>
/// <para>
/// The overloads without a context evaluate for the current request (<see cref="CreateContext"/>); the
/// overloads with a context evaluate for exactly that subject, which is how a handler or job evaluates a flag
/// for someone other than the caller.
/// </para>
/// <para>
/// A name the catalog does not contain — undeclared, or declared by a disabled module — evaluates as
/// <b>disabled</b> (no variant) and is logged once per name. The compiler rejects an undeclared name in
/// <c>[FeatureGate]</c>; the generated <c>ElarionFeatureFlags</c> keys make the imperative path just as checked.
/// </para>
/// </remarks>
public interface IFeatureFlagService {
    /// <summary>Builds the ambient evaluation context for the current request scope.</summary>
    FeatureEvaluationContext CreateContext();

    /// <summary>Returns whether <paramref name="flag"/> is enabled for the current request.</summary>
    ValueTask<bool> IsEnabledAsync(string flag, CancellationToken ct = default);

    /// <summary>Returns whether <paramref name="flag"/> is enabled for <paramref name="context"/>.</summary>
    ValueTask<bool> IsEnabledAsync(string flag, FeatureEvaluationContext context, CancellationToken ct = default);

    /// <summary>
    /// Returns the variant allocated to the current request for <paramref name="flag"/>, or
    /// <see langword="null"/> when the flag has no variant for it.
    /// </summary>
    ValueTask<string?> GetVariantAsync(string flag, CancellationToken ct = default);

    /// <summary>Returns the variant allocated to <paramref name="context"/> for <paramref name="flag"/>, or <see langword="null"/>.</summary>
    ValueTask<string?> GetVariantAsync(string flag, FeatureEvaluationContext context, CancellationToken ct = default);
}
