namespace Elarion.Abstractions.Features;

/// <summary>Reserved DI service keys used by variant-service registrations.</summary>
public static class VariantServiceKeys {
    /// <summary>
    /// The key the default variant implementation is registered under, so the resolver can fall back to it when
    /// no variant is allocated to the current user. Chosen to be collision-proof with real variant names.
    /// </summary>
    public const string Default = "__elarion_variant_default";
}

/// <summary>
/// Immutable per-contract binding: which feature flag drives the variant for <typeparamref name="TService"/> and
/// the DI key of the default implementation. Registered as a singleton by the variant-service generator (or
/// <c>AddElarionVariantService</c>) and consulted by <see cref="IVariantServiceProvider{TService}"/>.
/// </summary>
public sealed class VariantServiceBinding<TService> where TService : class {
    /// <summary>The feature flag whose allocated variant selects the implementation.</summary>
    public required string Feature { get; init; }

    /// <summary>
    /// The DI key of the default implementation, used when no variant is allocated. <c>null</c> when no default
    /// implementation was declared (resolution then yields nothing / throws).
    /// </summary>
    public string? DefaultKey { get; init; }
}
