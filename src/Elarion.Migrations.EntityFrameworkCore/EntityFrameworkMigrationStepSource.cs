using Elarion.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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

            ids = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).ToList();
        }

        string? previous = null;
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
            steps.Add(new EntityFrameworkMigrationStep<TContext>(id, versionPart, name, previous));
            previous = id;
        }

        return new MigrationStepSet { Steps = steps, Errors = errors };
    }
}

/// <summary>
/// One EF Core migration as a plan step. It executes the migration's own generated SQL — the same script
/// <c>dotnet ef migrations script</c> produces for that single migration — on the plan's connection inside the
/// plan's transaction, so the migration and the plan's history row commit atomically. EF's
/// <c>__EFMigrationsHistory</c> row is written by that script too, which keeps EF tooling truthful; the plan's
/// history stays the authority. On a database that EF migrated before the plan existed, the step is
/// satisfied — recorded without running — when <c>__EFMigrationsHistory</c> already lists its id.
/// </summary>
internal sealed class EntityFrameworkMigrationStep<TContext>(string migrationId, string version, string name,
    string? previousMigrationId) : MigrationStep where TContext : DbContext {
    public override string Kind => EntityFrameworkMigrationStepKinds.EntityFramework;

    public override string Name => migrationId;

    public override string? Version => version;

    public override string Description => name;

    public override async ValueTask<bool> IsAlreadySatisfiedAsync(MigrationStepContext context,
        CancellationToken cancellationToken) {
        var db = context.Services.GetRequiredService<TContext>();
        var history = db.GetService<IHistoryRepository>();
        var applied = await history.GetAppliedMigrationsAsync(cancellationToken);
        return applied.Any(row => string.Equals(row.MigrationId, migrationId, StringComparison.Ordinal));
    }

    public override async Task ExecuteAsync(MigrationStepContext context, CancellationToken cancellationToken) {
        var db = context.Services.GetRequiredService<TContext>();

        // NoTransactions: the plan owns the transaction, so the script must not open or close its own. The
        // history table is created first because a plan baselined past the first migration (or an EF history
        // dropped by hand) would otherwise fail on the history insert at the end of the script.
        var script = db.GetService<IMigrator>()
            .GenerateScript(previousMigrationId, migrationId, MigrationsSqlGenerationOptions.NoTransactions);
        var createHistory = db.GetService<IHistoryRepository>().GetCreateIfNotExistsScript();

        await context.ExecuteSqlAsync(createHistory + Environment.NewLine + script, cancellationToken);
    }
}
