namespace Elarion.Abstractions.Resilience;

/// <summary>
/// Identifies a named resilience policy registered with the framework.
/// </summary>
public readonly record struct ResiliencePolicyReference {
    /// <summary>The stable policy name.</summary>
    public required string Name { get; init; }

    /// <summary>Allows framework integrations to pass policy references to string-based APIs.</summary>
    public static implicit operator string(ResiliencePolicyReference reference) {
        return reference.Name;
    }
}
