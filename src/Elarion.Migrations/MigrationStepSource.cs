namespace Elarion.Migrations;

/// <summary>
/// Contributes steps to the migration plan (ADR-0081). The plan merges the steps of every source into one
/// version-ordered sequence; built-in sources are embedded SQL scripts (<see cref="MigrationOptions.AddScripts"/>)
/// and <see cref="ICodeMigration"/> implementations, and <c>Elarion.Migrations.EntityFrameworkCore</c> adds
/// EF Core migrations. A source is discovered on every run and must be side-effect free.
/// </summary>
public interface IMigrationStepSource {
    /// <summary>
    /// Enumerates this source's steps. Problems that make the source's content invalid (a malformed script
    /// name) are reported in <see cref="MigrationStepSet.Errors"/> — fail-closed and total, not thrown one at
    /// a time.
    /// </summary>
    /// <param name="services">
    /// The host's service provider (the root provider, or an empty one for a runner built without DI); create a
    /// scope from it for scoped services.
    /// </param>
    MigrationStepSet Discover(IServiceProvider services);
}

/// <summary>The steps one <see cref="IMigrationStepSource"/> contributed plus every problem it found.</summary>
public sealed record MigrationStepSet {
    /// <summary>The discovered steps, in any order — the plan sorts them by version.</summary>
    public required IReadOnlyList<MigrationStep> Steps { get; init; }

    /// <summary>Every discovery problem; non-empty blocks a migrate and is reported by validation.</summary>
    public IReadOnlyList<MigrationValidationError> Errors { get; init; } = [];
}
