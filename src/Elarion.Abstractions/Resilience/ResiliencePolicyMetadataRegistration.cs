namespace Elarion.Abstractions.Resilience;

/// <summary>
/// DI registration wrapper for source-generated resilience policy metadata.
/// </summary>
/// <remarks>
/// Generated policy registration emits this neutral metadata contract. A default Microsoft-backed
/// runtime or a custom runtime can both consume the same registrations.
/// </remarks>
public sealed record ResiliencePolicyMetadataRegistration {
    /// <summary>The generated policy metadata.</summary>
    public required ResiliencePolicyMetadata Metadata { get; init; }
}
