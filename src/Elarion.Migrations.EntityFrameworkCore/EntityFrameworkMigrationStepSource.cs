using System.Reflection;
using Elarion.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Migrations.EntityFrameworkCore;

/// <summary>The <see cref="MigrationStep.Kind"/> of an EF Core migration step.</summary>
public static class EntityFrameworkMigrationStepKinds {
    /// <summary>An EF Core migration (<c>ef</c>).</summary>
    public const string EntityFramework = "ef";
}

/// <summary>
/// Contributes every EF Core migration of <typeparamref name="TContext"/> to the migration plan (ADR-0081).
/// Each migration becomes one step versioned by the timestamp prefix of its id
/// (<c>20260713093000_AddDevices</c> → <c>20260713093000</c>) — the same space SQL scripts and code migrations
/// are versioned in, so they interleave by timestamp.
/// </summary>
/// <typeparam name="TContext">The <see cref="DbContext"/> whose migrations are contributed; resolved from the root provider at discovery.</typeparam>
internal sealed class EntityFrameworkMigrationStepSource<TContext> : IMigrationStepSource where TContext : DbContext {
    public MigrationStepSet Discover(IServiceProvider services) {
        var errors = new List<MigrationValidationError>();
        var steps = new List<MigrationStep>();
        var suppressing = new HashSet<string>(StringComparer.Ordinal);

        IReadOnlyList<string> ids;
        using (var scope = services.CreateScope()) {
            var context = scope.ServiceProvider.GetService<TContext>();
            if (context is null) {
                return new MigrationStepSet {
                    Steps = steps,
                    Errors = [
                        new MigrationValidationError {
                            Message =
                                $"EF migrations need {typeof(TContext).FullName} registered in the service provider (AddDbContext), but it could not be resolved."
                        }
                    ]
                };
            }

            var assembly = context.GetService<IMigrationsAssembly>();
            ids = assembly.Migrations.Keys.Order(StringComparer.Ordinal).ToList();
            var provider = context.Database.ProviderName;
            foreach (var id in ids) {
                // A migration that suppresses the transaction (CREATE INDEX CONCURRENTLY and friends) cannot join the
                // plan's transaction, so its step opts out of one — the plan's rule for such work (ADR-0081).
                var migration = assembly.CreateMigration(assembly.Migrations[id], provider!);
                if (migration.UpOperations.OfType<SqlOperation>().Any(o => o.SuppressTransaction)) suppressing.Add(id);
            }
        }

        foreach (var id in ids) {
            var separator = id.IndexOf('_', StringComparison.Ordinal);
            var versionPart = separator < 0 ? id : id[..separator];
            if (versionPart.Length == 0 || !versionPart.All(char.IsAsciiDigit)) {
                errors.Add(new MigrationValidationError {
                    StepName = id,
                    Message =
                        $"EF migration id '{id}' has no numeric timestamp prefix ('{{timestamp}}_{{Name}}'), so it cannot be ordered against SQL scripts and code migrations."
                });
                continue;
            }

            var name = separator < 0 || separator + 1 >= id.Length ? id : id[(separator + 1)..];
            steps.Add(new EntityFrameworkMigrationStep<TContext>(id, versionPart, name, !suppressing.Contains(id)));
        }

        return new MigrationStepSet { Steps = steps, Errors = errors };
    }
}

/// <summary>
/// One EF Core migration as a plan step. It executes the migration's own generated commands — the ones
/// <c>Database.MigrateAsync()</c> executes — on the plan's connection inside the plan's transaction, so the
/// migration and the plan's history row commit atomically. Every command runs as its own statement, exactly as
/// EF's migrator runs it, so a raw <c>migrationBuilder.Sql("...")</c> without a trailing <c>;</c> cannot run into
/// the next operation (a concatenated script would). EF's <c>__EFMigrationsHistory</c> row is written too, which
/// keeps EF tooling truthful; the plan's history stays the authority. A migration whose raw SQL operations
/// suppress the transaction makes the step non-transactional; any other command that asks for it fails the step. On a database that EF migrated before the plan existed, the step is
/// satisfied — recorded without running — when <c>__EFMigrationsHistory</c> already lists its id.
/// </summary>
internal sealed class EntityFrameworkMigrationStep<TContext>(string migrationId, string version, string name,
    bool useTransaction) : MigrationStep where TContext : DbContext {
    public override string Kind => EntityFrameworkMigrationStepKinds.EntityFramework;

    public override string Name => migrationId;

    public override string? Version => version;

    public override string Description => name;

    public override bool UseTransaction => useTransaction;

    public override async ValueTask<bool> IsAlreadySatisfiedAsync(MigrationStepContext context,
        CancellationToken cancellationToken) {
        var db = context.Services.GetRequiredService<TContext>();
        var history = db.GetService<IHistoryRepository>();
        var applied = await history.GetAppliedMigrationsAsync(cancellationToken);
        return applied.Any(row => string.Equals(row.MigrationId, migrationId, StringComparison.Ordinal));
    }

    public override async Task ExecuteAsync(MigrationStepContext context, CancellationToken cancellationToken) {
        var db = context.Services.GetRequiredService<TContext>();
        var history = db.GetService<IHistoryRepository>();
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[migrationId], db.Database.ProviderName!);
        var model = db.GetService<IModelRuntimeInitializer>()
            .Initialize(FinalizeModel(migration.TargetModel), designTime: true, validationLogger: null);

        // The history table is created first because a plan baselined past the first migration (or an EF history
        // dropped by hand) would otherwise fail on the history insert at the end of the migration.
        await context.ExecuteSqlAsync(history.GetCreateIfNotExistsScript(), cancellationToken);

        var commands = db.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.UpOperations, model, MigrationsSqlGenerationOptions.Default);
        foreach (var command in commands) {
            if (command.TransactionSuppressed && context.Transaction is not null) {
                throw new InvalidOperationException(
                    $"EF migration '{migrationId}' contains a command that must run outside a transaction, but the plan runs this step in one. Express it as a raw migrationBuilder.Sql(..., suppressTransaction: true) operation so the step opts out of the transaction, or move it to a SQL script with a '-- elarion: no-transaction' directive.");
            }

            await context.ExecuteSqlAsync(command.CommandText, cancellationToken);
        }

        var row = new HistoryRow(migrationId, EfProductVersion);
        await context.ExecuteSqlAsync(history.GetInsertScript(row), cancellationToken);
    }

    private static IModel FinalizeModel(IModel model) {
        return model is IMutableModel mutable ? mutable.FinalizeModel() : model;
    }

    private static string EfProductVersion { get; } = typeof(DbContext).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
}
