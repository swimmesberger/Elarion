namespace Elarion.Migrations;

/// <summary>An <see cref="ICodeMigration"/> adapted to a plan step.</summary>
internal sealed class CodeMigrationStep(ICodeMigration migration) : MigrationStep {
    public override string Kind => MigrationStepKinds.Code;

    public override string Name { get; } = migration.GetType().Name;

    public override string? Version => migration.Version;

    public override string Description => migration.Description;

    public override string Origin { get; } = migration.GetType().FullName ?? migration.GetType().Name;

    public override bool UseTransaction => migration.UseTransaction;

    public override ValueTask<bool> IsAlreadySatisfiedAsync(MigrationStepContext context,
        CancellationToken cancellationToken) {
        return migration.IsAlreadySatisfiedAsync(context, cancellationToken);
    }

    public override Task ExecuteAsync(MigrationStepContext context, CancellationToken cancellationToken) {
        return migration.ExecuteAsync(context, cancellationToken);
    }
}

/// <summary>The <see cref="ICodeMigration"/> step source: the registered code migrations, validated.</summary>
internal sealed class CodeMigrationStepSource(IEnumerable<ICodeMigration> migrations) : IMigrationStepSource {
    public MigrationStepSet Discover(IServiceProvider services) {
        var steps = new List<MigrationStep>();
        var errors = new List<MigrationValidationError>();
        foreach (var migration in migrations) {
            var type = migration.GetType().FullName ?? migration.GetType().Name;
            if (string.IsNullOrWhiteSpace(migration.Version) || !MigrationVersion.TryParse(migration.Version, out _)) {
                errors.Add(new MigrationValidationError {
                    StepName = type,
                    Message =
                        $"Code migration {type} has an invalid Version '{migration.Version}'; expected numeric segments separated by '.' or '_' (e.g. '20260901120000')."
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(migration.Description)) {
                errors.Add(new MigrationValidationError {
                    StepName = type, Message = $"Code migration {type} has a blank Description."
                });
                continue;
            }

            steps.Add(new CodeMigrationStep(migration));
        }

        return new MigrationStepSet { Steps = steps, Errors = errors };
    }
}
