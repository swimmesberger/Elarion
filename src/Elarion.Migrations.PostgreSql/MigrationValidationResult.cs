namespace Elarion.Migrations.PostgreSql;

/// <summary>One problem found by <see cref="IMigrationRunner.ValidateAsync"/>.</summary>
public sealed record MigrationValidationError {
    /// <summary>The script (file name or resource name) the problem concerns, when attributable to one.</summary>
    public string? ScriptName { get; init; }

    /// <summary>What is wrong and how to resolve it.</summary>
    public required string Message { get; init; }
}

/// <summary>The point-in-time report produced by <see cref="IMigrationRunner.ValidateAsync"/>; never writes.</summary>
public sealed record MigrationValidationResult {
    /// <summary>
    /// Everything that would make <see cref="IMigrationRunner.MigrateAsync"/> fail before applying
    /// anything: invalid script resources, checksum mismatches, unresolved failed migrations, and
    /// out-of-order scripts under <see cref="OutOfOrderPolicy.Deny"/>.
    /// </summary>
    public required IReadOnlyList<MigrationValidationError> Errors { get; init; }

    /// <summary>The scripts a <see cref="IMigrationRunner.MigrateAsync"/> would apply, in execution order.</summary>
    public required IReadOnlyList<MigrationScriptInfo> Pending { get; init; }

    /// <summary>Whether <see cref="Errors"/> is empty.</summary>
    public bool IsValid => Errors.Count == 0;
}
