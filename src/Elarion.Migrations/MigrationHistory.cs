namespace Elarion.Migrations;

/// <summary>The wire values of the history table's <c>outcome</c> column.</summary>
internal static class MigrationOutcomes {
    public const string Applied = "applied";
    public const string Satisfied = "satisfied";
    public const string Failed = "failed";
    public const string Baseline = "baseline";

    public static string ToWire(MigrationOutcome outcome) {
        return outcome switch {
            MigrationOutcome.Applied => Applied,
            MigrationOutcome.Satisfied => Satisfied,
            MigrationOutcome.Baseline => Baseline,
            MigrationOutcome.Failed => Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };
    }
}

/// <summary>
/// One row read back from the history table — the fields the planner reasons over. A provider's
/// <see cref="IMigrationSession.LoadHistoryAsync"/> materializes these; write-only columns
/// (<c>applied_at</c>, <c>duration_ms</c>) are deliberately absent.
/// </summary>
public sealed record AppliedMigrationRow {
    /// <summary>The monotonic execution order the row was written in.</summary>
    public required int InstalledRank { get; init; }

    /// <summary>The step kind (<c>sql</c>, <c>code</c>, <c>ef</c>, or <c>baseline</c> for a baseline marker).</summary>
    public required string Kind { get; init; }

    /// <summary>The canonical dotted version, or <see langword="null"/> for a repeatable row.</summary>
    public required string? Version { get; init; }

    /// <summary>The description recorded for the row.</summary>
    public required string Description { get; init; }

    /// <summary>The step name the row was written for.</summary>
    public required string StepName { get; init; }

    /// <summary>The recorded checksum, or <see langword="null"/> for a step without content or a baseline row.</summary>
    public required string? Checksum { get; init; }

    /// <summary>The row outcome: <c>applied</c>, <c>satisfied</c>, <c>failed</c>, or <c>baseline</c>.</summary>
    public required string Outcome { get; init; }
}

/// <summary>
/// A history row to write, handed by the neutral <see cref="MigrationRunner"/> to an
/// <see cref="IMigrationSession"/> for insertion. A provider persists the fields verbatim (the server supplies
/// the applied-at timestamp).
/// </summary>
public sealed record MigrationHistoryRecord {
    /// <summary>The monotonic execution order to record.</summary>
    public required int InstalledRank { get; init; }

    /// <summary>The step kind to record.</summary>
    public required string Kind { get; init; }

    /// <summary>The canonical dotted version, or <see langword="null"/> for a repeatable row.</summary>
    public required string? Version { get; init; }

    /// <summary>The description to record.</summary>
    public required string Description { get; init; }

    /// <summary>The step name to record.</summary>
    public required string StepName { get; init; }

    /// <summary>The checksum to record, or <see langword="null"/>.</summary>
    public required string? Checksum { get; init; }

    /// <summary>The outcome to record: <c>applied</c>, <c>satisfied</c>, <c>failed</c>, or <c>baseline</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>The measured execution time, in milliseconds.</summary>
    public required long DurationMs { get; init; }
}
