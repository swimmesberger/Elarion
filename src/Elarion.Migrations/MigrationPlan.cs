namespace Elarion.Migrations;

/// <summary>A step with its parsed version, as the plan orders it.</summary>
internal sealed record PlannedStep(MigrationStep Step, MigrationVersion? Version) {
    public MigrationStepInfo ToInfo() {
        return new MigrationStepInfo {
            Kind = Step.Kind,
            Name = Step.Name,
            Version = Version?.Text,
            Description = Step.Description,
            Checksum = Step.Checksum
        };
    }
}

/// <summary>The merged steps of every source: one version-ordered sequence plus every discovery problem.</summary>
internal sealed record MigrationStepCatalog {
    /// <summary>Versioned steps of all kinds in one ascending version order.</summary>
    public required IReadOnlyList<PlannedStep> Versioned { get; init; }

    /// <summary>Repeatable steps, sorted by name (ordinal).</summary>
    public required IReadOnlyList<PlannedStep> Repeatable { get; init; }

    /// <summary>Every discovery problem: source errors, unparseable versions, duplicates across all kinds.</summary>
    public required IReadOnlyList<MigrationValidationError> Errors { get; init; }

    public static MigrationStepCatalog Build(IEnumerable<IMigrationStepSource> sources, IServiceProvider services) {
        var errors = new List<MigrationValidationError>();
        var planned = new List<PlannedStep>();

        foreach (var source in sources) {
            var set = source.Discover(services);
            errors.AddRange(set.Errors);
            foreach (var step in set.Steps) {
                if (step.Version is null) {
                    planned.Add(new PlannedStep(step, null));
                    continue;
                }

                if (!MigrationVersion.TryParse(step.Version, out var version)) {
                    errors.Add(new MigrationValidationError {
                        StepName = step.Origin,
                        Message = $"Migration step '{step.Origin}' has an invalid version '{step.Version}'."
                    });
                    continue;
                }

                planned.Add(new PlannedStep(step, version));
            }
        }

        // One version space for every kind: a SQL script and a code step may not claim the same version, or the
        // history could not tell which one ran.
        foreach (var group in planned.Where(p => p.Version is not null).GroupBy(p => p.Version!)
                     .Where(g => g.Count() > 1)) {
            var origins = string.Join(", ", group.Select(p => $"'{p.Step.Origin}'"));
            errors.Add(new MigrationValidationError {
                Message =
                    $"Duplicate migration version {group.Key.Text}: {origins}. Each version must exist exactly once across all steps."
            });
        }

        foreach (var group in planned.Where(p => p.Version is null).GroupBy(p => p.Step.Name, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)) {
            var origins = string.Join(", ", group.Select(p => $"'{p.Step.Origin}'"));
            errors.Add(new MigrationValidationError {
                StepName = group.Key,
                Message =
                    $"Duplicate repeatable migration '{group.Key}': {origins}. Repeatable script file names must be unique."
            });
        }

        return new MigrationStepCatalog {
            Versioned = planned.Where(p => p.Version is not null).OrderBy(p => p.Version).ToList(),
            Repeatable = planned.Where(p => p.Version is null).OrderBy(p => p.Step.Name, StringComparer.Ordinal)
                .ToList(),
            Errors = errors
        };
    }
}

/// <summary>What a run would do, given the discovered steps and the current history.</summary>
internal sealed record MigrationPlan {
    /// <summary>Pending versioned steps in version order, all kinds interleaved (out-of-order ones included).</summary>
    public required IReadOnlyList<PlannedStep> PendingVersioned { get; init; }

    /// <summary>The subset of <see cref="PendingVersioned"/> versioned below an already-applied version.</summary>
    public required IReadOnlyList<PlannedStep> OutOfOrder { get; init; }

    /// <summary>Repeatable steps whose checksum changed (or that never ran), in name order.</summary>
    public required IReadOnlyList<PlannedStep> PendingRepeatable { get; init; }

    /// <summary>Checksum/kind mismatches and corrupt history rows — each blocks a migrate.</summary>
    public required IReadOnlyList<MigrationValidationError> Errors { get; init; }

    /// <summary>Unresolved failed rows from earlier non-transactional steps — each blocks a migrate.</summary>
    public required IReadOnlyList<AppliedMigrationRow> FailedRows { get; init; }

    public IEnumerable<PlannedStep> Pending => PendingVersioned.Concat(PendingRepeatable);
}

/// <summary>Pure planning shared by migrate, validate, and pending queries.</summary>
internal static class MigrationPlanner {
    public static MigrationPlan Build(MigrationStepCatalog steps, IReadOnlyList<AppliedMigrationRow> history) {
        var errors = new List<MigrationValidationError>();
        var failedRows = new List<AppliedMigrationRow>();
        var appliedVersions = new Dictionary<MigrationVersion, AppliedMigrationRow>();
        MigrationVersion? baseline = null;
        MigrationVersion? maxKnown = null;

        foreach (var row in history) {
            if (row.Outcome == MigrationOutcomes.Failed) failedRows.Add(row);

            if (row.Version is null) continue;

            if (!MigrationVersion.TryParse(row.Version, out var version)) {
                errors.Add(new MigrationValidationError {
                    StepName = row.StepName,
                    Message = $"History row {row.InstalledRank} has an unparseable version '{row.Version}'."
                });
                continue;
            }

            appliedVersions[version] = row;
            if (row.Outcome == MigrationOutcomes.Baseline && (baseline is null || version.CompareTo(baseline) > 0))
                baseline = version;

            if (maxKnown is null || version.CompareTo(maxKnown) > 0) maxKnown = version;
        }

        var pendingVersioned = new List<PlannedStep>();
        var outOfOrder = new List<PlannedStep>();
        foreach (var planned in steps.Versioned) {
            var step = planned.Step;
            if (appliedVersions.TryGetValue(planned.Version!, out var row)) {
                // A baseline row is a prefix marker, not an identity: only a real row pins kind and content.
                if (row.Outcome != MigrationOutcomes.Baseline && row.Kind != step.Kind)
                    errors.Add(new MigrationValidationError {
                        StepName = step.Name,
                        Message = $"Version {planned.Version!.Text} was recorded for a '{row.Kind}' step "
                                  + $"('{row.StepName}') but is now claimed by the '{step.Kind}' step '{step.Name}'. "
                                  + "A version identifies one step forever; give the new step its own version."
                    });
                // Only applied rows are checksum-guarded: editing a failed script is the legitimate fix path,
                // and baseline/satisfied rows never had this content.
                else if (row.Outcome == MigrationOutcomes.Applied && row.Checksum is not null
                                                                  && step.Checksum is not null
                                                                  && row.Checksum != step.Checksum)
                    errors.Add(new MigrationValidationError {
                        StepName = step.Name,
                        Message = $"Checksum mismatch for applied migration '{step.Name}': "
                                  + $"applied {row.Checksum}, resource {step.Checksum}. "
                                  + "An applied script was edited; either revert the edit or add a new migration script with the change."
                    });

                continue;
            }

            // Versions at or below an explicit baseline are the schema the baseline declared already present.
            if (baseline is not null && planned.Version!.CompareTo(baseline) <= 0) continue;

            pendingVersioned.Add(planned);
            if (maxKnown is not null && planned.Version!.CompareTo(maxKnown) < 0) outOfOrder.Add(planned);
        }

        // A repeatable reruns when its latest recorded checksum (highest rank per name) differs.
        var latestRepeatable = new Dictionary<string, AppliedMigrationRow>(StringComparer.Ordinal);
        foreach (var row in history)
            if (row.Version is null && row.Outcome == MigrationOutcomes.Applied)
                latestRepeatable[row.StepName] = row;

        var pendingRepeatable = steps.Repeatable
            .Where(planned => !latestRepeatable.TryGetValue(planned.Step.Name, out var row) ||
                              row.Checksum != planned.Step.Checksum)
            .ToList();

        return new MigrationPlan {
            PendingVersioned = pendingVersioned,
            OutOfOrder = outOfOrder,
            PendingRepeatable = pendingRepeatable,
            Errors = errors,
            FailedRows = failedRows
        };
    }
}
