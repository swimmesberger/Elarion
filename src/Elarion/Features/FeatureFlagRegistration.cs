using Elarion.Abstractions.Features;

namespace Elarion.Features;

/// <summary>
/// One flag's descriptor plus the way to reach its owner: the resolver factory for a code-defined flag, or
/// <see langword="null"/> for a backend-evaluated flag (the host's <see cref="IBackendFeatureFlagEvaluator"/> owns it).
/// Registered by the generated per-module wiring (or by hand through <c>AddElarionFeatureFlag</c>) and read once
/// by <see cref="FeatureFlagCatalog"/>.
/// </summary>
internal sealed record FeatureFlagRegistration(
    FeatureFlagDescriptor Descriptor,
    Func<FeatureEvaluationContext, IFeatureFlagResolver>? ResolverFactory);
