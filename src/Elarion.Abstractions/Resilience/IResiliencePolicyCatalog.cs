namespace Elarion.Abstractions.Resilience;

/// <summary>
/// Provides framework-owned metadata for generated resilience policies.
/// </summary>
public interface IResiliencePolicyCatalog {
    /// <summary>Returns metadata for a named policy, or null when the policy was registered manually without metadata.</summary>
    ResiliencePolicyMetadata? GetPolicy(ResiliencePolicyReference policy);
}
