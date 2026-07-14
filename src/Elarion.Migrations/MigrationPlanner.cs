namespace Elarion.Migrations;

/// <summary>What a run would do, given the discovered scripts and the current history.</summary>
internal sealed record MigrationPlan {
    /// <summary>Pending versioned scripts in version order (out-of-order ones included).</summary>
    public required IReadOnlyList<MigrationScript> PendingVersioned { get; init; }

    /// <summary>The subset of <see cref="PendingVersioned"/> versioned below an already-applied version.</summary>
    public required IReadOnlyList<MigrationScript> OutOfOrder { get; init; }

    /// <summary>Repeatable scripts whose checksum changed (or that never ran), in name order.</summary>
    public required IReadOnlyList<MigrationScript> PendingRepeatable { get; init; }

    /// <summary>Checksum mismatches and corrupt history rows — each blocks a migrate.</summary>
    public required IReadOnlyList<MigrationValidationError> Errors { get; init; }

    /// <summary>Unresolved failed rows from earlier no-transaction migrations — each blocks a migrate.</summary>
    public required IReadOnlyList<AppliedMigrationRow> FailedRows { get; init; }
}

/// <summary>Pure planning shared by migrate, validate, and pending queries.</summary>
internal static class MigrationPlanner {
    public static MigrationPlan Build(MigrationScriptSet scripts, IReadOnlyList<AppliedMigrationRow> history) {
        var errors = new List<MigrationValidationError>();
        var failedRows = new List<AppliedMigrationRow>();
        var appliedVersions = new Dictionary<MigrationVersion, AppliedMigrationRow>();
        MigrationVersion? baseline = null;
        MigrationVersion? maxKnown = null;

        foreach (var row in history) {
            if (row.State == MigrationStates.Failed) {
                failedRows.Add(row);
            }

            if (row.Version is null) {
                continue;
            }

            if (!MigrationVersion.TryParse(row.Version, out var version)) {
                errors.Add(new MigrationValidationError {
                    ScriptName = row.ScriptName,
                    Message = $"History row {row.InstalledRank} has an unparseable version '{row.Version}'.",
                });
                continue;
            }

            appliedVersions[version] = row;
            if (row.State == MigrationStates.Baseline && (baseline is null || version.CompareTo(baseline) > 0)) {
                baseline = version;
            }

            if (maxKnown is null || version.CompareTo(maxKnown) > 0) {
                maxKnown = version;
            }
        }

        var pendingVersioned = new List<MigrationScript>();
        var outOfOrder = new List<MigrationScript>();
        foreach (var script in scripts.Versioned) {
            if (appliedVersions.TryGetValue(script.Version!, out var row)) {
                // Only applied rows are checksum-guarded: editing a failed script is the legitimate fix
                // path, and a baseline row never had content.
                if (row.State == MigrationStates.Applied && row.Checksum is not null && row.Checksum != script.Checksum) {
                    errors.Add(new MigrationValidationError {
                        ScriptName = script.ScriptName,
                        Message = $"Checksum mismatch for applied migration '{script.ScriptName}': "
                            + $"applied {row.Checksum}, resource {script.Checksum}. "
                            + "An applied script was edited; either revert the edit or add a new migration script with the change.",
                    });
                }

                continue;
            }

            // Versions at or below an explicit baseline are the schema the baseline declared already present.
            if (baseline is not null && script.Version!.CompareTo(baseline) <= 0) {
                continue;
            }

            pendingVersioned.Add(script);
            if (maxKnown is not null && script.Version!.CompareTo(maxKnown) < 0) {
                outOfOrder.Add(script);
            }
        }

        // A repeatable reruns when its latest recorded checksum (highest rank per script name) differs.
        var latestRepeatable = new Dictionary<string, AppliedMigrationRow>(StringComparer.Ordinal);
        foreach (var row in history) {
            if (row.Version is null && row.State == MigrationStates.Applied) {
                latestRepeatable[row.ScriptName] = row;
            }
        }

        var pendingRepeatable = scripts.Repeatable
            .Where(script => !latestRepeatable.TryGetValue(script.ScriptName, out var row) || row.Checksum != script.Checksum)
            .ToList();

        return new MigrationPlan {
            PendingVersioned = pendingVersioned,
            OutOfOrder = outOfOrder,
            PendingRepeatable = pendingRepeatable,
            Errors = errors,
            FailedRows = failedRows,
        };
    }
}
