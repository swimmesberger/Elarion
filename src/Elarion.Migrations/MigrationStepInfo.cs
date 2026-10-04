namespace Elarion.Migrations;

/// <summary>Describes one step of the migration plan.</summary>
public sealed record MigrationStepInfo {
    /// <summary>The step kind (<see cref="MigrationStepKinds"/>).</summary>
    public required string Kind { get; init; }

    /// <summary>The step name: script file name, migration type name, EF migration id.</summary>
    public required string Name { get; init; }

    /// <summary>The version in canonical dotted form (e.g. <c>1.2</c>), or <see langword="null"/> for a repeatable step.</summary>
    public string? Version { get; init; }

    /// <summary>The step description.</summary>
    public required string Description { get; init; }

    /// <summary>The content checksum (lowercase hex SHA-256 for scripts), or <see langword="null"/> for steps without content.</summary>
    public string? Checksum { get; init; }

    /// <summary>Whether this is a repeatable step.</summary>
    public bool IsRepeatable => Version is null;
}

/// <summary>How a step ended up in the history.</summary>
public enum MigrationOutcome {
    /// <summary>The step ran and completed.</summary>
    Applied,

    /// <summary>
    /// <see cref="MigrationStep.IsAlreadySatisfiedAsync"/> reported nothing to do; recorded without running
    /// (a fresh install, or an adopted database that already has the change).
    /// </summary>
    Satisfied,

    /// <summary>The step was declared already present by <see cref="IMigrationRunner.BaselineAsync"/>.</summary>
    Baseline,

    /// <summary>A non-transactional step failed half-applied and awaits <see cref="IMigrationRunner.ResolveFailedAsync"/>.</summary>
    Failed
}

/// <summary>One step a <see cref="IMigrationRunner.MigrateAsync"/> run recorded.</summary>
public sealed record MigrationStepResult {
    /// <summary>The step.</summary>
    public required MigrationStepInfo Step { get; init; }

    /// <summary><see cref="MigrationOutcome.Applied"/> or <see cref="MigrationOutcome.Satisfied"/>.</summary>
    public required MigrationOutcome Outcome { get; init; }

    /// <summary>How long the step took, including its history write.</summary>
    public required TimeSpan Duration { get; init; }
}
