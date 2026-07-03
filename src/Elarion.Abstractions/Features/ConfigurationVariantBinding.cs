namespace Elarion.Abstractions.Features;

/// <summary>
/// Immutable per-contract binding: which configuration key drives the variant for
/// <typeparamref name="TService"/> and the DI key of the default implementation. Registered as a singleton by
/// the variant-service generator (or <c>AddElarionConfigurationVariantService</c>) and consulted on every
/// transparent resolution of the contract.
/// </summary>
public sealed class ConfigurationVariantBinding<TService> where TService : class {
    /// <summary>The configuration key whose value selects the implementation.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// The DI key of the default implementation, used when the configuration key is absent or its value matches
    /// no registered variant. <c>null</c> when no default implementation was declared (resolution then yields
    /// nothing / throws).
    /// </summary>
    public string? DefaultKey { get; init; }
}
