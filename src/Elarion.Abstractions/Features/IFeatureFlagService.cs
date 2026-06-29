namespace Elarion.Abstractions.Features;

/// <summary>
/// The transport-neutral feature-flag seam Elarion gates handlers on, and that application code can inject to
/// check a flag imperatively at run time. It deliberately exposes only the boolean enablement question — the
/// 80% case — keeping the abstraction AOT-clean and provider-agnostic so the backend can be Microsoft
/// <c>FeatureManagement</c> (the shipped default, <c>Elarion.FeatureManagement</c>), OpenFeature, LaunchDarkly,
/// ConfigCat, Unleash, Flagsmith, or a custom store by registering a different implementation.
/// </summary>
/// <remarks>
/// <para>
/// This is the feature-flag analog of <c>IHandlerCache</c>/<c>IAuthorizer</c>: the contract and the
/// <see cref="FeatureGateDecorator{TRequest, TResponse}"/> live in <c>Elarion.Abstractions</c> (which must stay
/// free of any runtime feature-management dependency), while the concrete provider binding lives one layer up in
/// an opt-in package.
/// </para>
/// <para>
/// Targeting context (which user/segment a flag is evaluated for) is <b>ambient</b>, not a parameter: the default
/// provider maps Elarion's <c>ICurrentUser</c> (user id + roles) into the backend's targeting model, so
/// percentage/targeting rollouts work the same off-HTTP as on. Multivariate/variant evaluation is intentionally
/// out of scope for this seam; add a dedicated accessor if a backend's variants are needed.
/// </para>
/// </remarks>
public interface IFeatureFlagService {
    /// <summary>
    /// Returns whether <paramref name="feature"/> is enabled for the current ambient targeting context.
    /// </summary>
    /// <param name="feature">The feature flag name.</param>
    /// <param name="ct">A token to cancel the (potentially asynchronous) evaluation.</param>
    ValueTask<bool> IsEnabledAsync(string feature, CancellationToken ct = default);
}
