namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Source-generated stable reference to a scheduled job name.
/// </summary>
public readonly record struct ScheduledJobReference {
    /// <summary>The stable job name used by the scheduler.</summary>
    public required string Name { get; init; }

    /// <inheritdoc />
    public override string ToString() {
        return Name;
    }

    /// <summary>Converts the reference to its stable job name.</summary>
    public static implicit operator string(ScheduledJobReference reference) {
        return reference.Name;
    }
}
