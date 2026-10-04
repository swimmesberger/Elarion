using Elarion.Migrations;
using Microsoft.EntityFrameworkCore;

namespace Elarion.Migrations.EntityFrameworkCore;

/// <summary>Registers EF Core migrations as a step source of the Elarion migration plan (ADR-0081).</summary>
public static class EntityFrameworkMigrationsOptionsExtensions {
    /// <summary>
    /// Adds every EF Core migration of <typeparamref name="TContext"/> to the plan as one step each, ordered by
    /// the timestamp in the migration id against the application's SQL scripts and code migrations. An EF host
    /// then runs the single Elarion migration runner at startup instead of <c>Database.MigrateAsync()</c>:
    /// one history, one advisory lock, one ordering across expand migrations, C# backfills and contract
    /// migrations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The context must be registered (<c>AddDbContext</c>) and its provider's migration script must be a plain
    /// statement batch — true for PostgreSQL and SQLite. Each EF migration runs in a transaction, so an
    /// operation EF marks as non-transactional (<c>suppressTransaction</c>, such as
    /// <c>CREATE INDEX CONCURRENTLY</c>) belongs in a <c>-- elarion: no-transaction</c> SQL script instead.
    /// </para>
    /// <para>
    /// A database that EF already migrated needs no baseline: each EF step whose id is in
    /// <c>__EFMigrationsHistory</c> is recorded as satisfied the first time the plan runs.
    /// </para>
    /// </remarks>
    /// <typeparam name="TContext">The application's <see cref="DbContext"/>.</typeparam>
    /// <param name="options">The migration options.</param>
    /// <returns>The same options instance for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionPostgreSql(connectionString);
    /// builder.Services.AddCodeMigration&lt;BackfillOrderTotals&gt;();
    /// builder.Services.AddElarionMigrations(o => o.AddEntityFrameworkMigrations&lt;AppDbContext&gt;());
    /// </code>
    /// </example>
    public static MigrationOptions AddEntityFrameworkMigrations<TContext>(this MigrationOptions options)
        where TContext : DbContext {
        ArgumentNullException.ThrowIfNull(options);
        return options.AddStepSource(new EntityFrameworkMigrationStepSource<TContext>());
    }
}
