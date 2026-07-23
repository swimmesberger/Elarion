using System.Reflection;

namespace Elarion.Migrations;

/// <summary>One assembly whose embedded resources are scanned for migration scripts.</summary>
/// <param name="Assembly">The assembly carrying the embedded <c>.sql</c> resources.</param>
/// <param name="ResourceNamePrefix">
/// Optional ordinal prefix limiting the scan (e.g. <c>"MyApp.Migrations."</c>). Without it every
/// embedded <c>.sql</c> resource of the assembly must be a valid migration script.
/// </param>
public sealed record MigrationScriptSource(Assembly Assembly, string? ResourceNamePrefix);

/// <summary>
/// The database-neutral migration options (ADR-0060) — script sources, history-table name, out-of-order
/// policy, timeouts, and startup application. Used directly by the neutral <c>AddElarionMigrations</c>
/// registration; a provider may still extend it with engine-specific knobs where a separate options type
/// is warranted (a PostgreSQL advisory-lock key is a provider-registration argument, not an option here).
/// </summary>
public class MigrationOptions {
    private readonly List<MigrationScriptSource> _scriptSources = [];

    /// <summary>The assemblies scanned for embedded migration scripts, in registration order.</summary>
    public IReadOnlyList<MigrationScriptSource> ScriptSources => _scriptSources;

    /// <summary>
    /// The history table the runner creates and maintains. A plain identifier, created in the schema the
    /// connection points at (see the provider's schema handling); defaults to
    /// <c>elarion_schema_history</c>.
    /// </summary>
    public string HistoryTableName { get; set; } = "elarion_schema_history";

    /// <summary>
    /// What to do with a pending script versioned below an already-applied one. Defaults to
    /// <see cref="OutOfOrderPolicy.Warn"/>: apply, warn, record in true execution order.
    /// </summary>
    public OutOfOrderPolicy OutOfOrder { get; set; } = OutOfOrderPolicy.Warn;

    /// <summary>
    /// The per-command timeout: a transactional script executes as one command, a
    /// <c>no-transaction</c> script one command per statement. Defaults to <see langword="null"/> — no
    /// timeout — because long-running DDL is normal for migrations. Non-positive values (e.g.
    /// <see cref="Timeout.InfiniteTimeSpan"/>) also mean no timeout.
    /// </summary>
    public TimeSpan? CommandTimeout { get; set; }

    /// <summary>
    /// How long to wait for the migration lock when another runner holds it. Defaults to
    /// <see langword="null"/> — wait indefinitely — so concurrent startups serialize behind a long
    /// migration instead of failing. Non-positive values (e.g. <see cref="Timeout.InfiniteTimeSpan"/>)
    /// also mean wait indefinitely.
    /// </summary>
    public TimeSpan? LockTimeout { get; set; }

    /// <summary>
    /// Whether the provider's <c>AddElarion…Migrations</c> also registers the hosted service that runs
    /// <see cref="IMigrationRunner.MigrateAsync"/> before the host reports ready (and fails startup on a
    /// migration error). Defaults to <see langword="true"/>; disable for hosts that invoke the runner
    /// themselves (a deploy tool, a manual gate).
    /// </summary>
    public bool ApplyOnStartup { get; set; } = true;

    /// <summary>
    /// Adds an assembly whose embedded resources are scanned for migration scripts. Every resource
    /// ending in <c>.sql</c> under <paramref name="resourceNamePrefix"/> (or the whole assembly when
    /// omitted) must be a valid <c>V{version}__{description}.sql</c> or <c>R__{description}.sql</c>
    /// script — validation is fail-closed, nothing is silently skipped.
    /// </summary>
    /// <param name="assembly">The assembly carrying the scripts.</param>
    /// <param name="resourceNamePrefix">Optional ordinal resource-name prefix limiting the scan.</param>
    /// <returns>The same options instance for chaining.</returns>
    public MigrationOptions AddScripts(Assembly assembly, string? resourceNamePrefix = null) {
        ArgumentNullException.ThrowIfNull(assembly);
        _scriptSources.Add(new MigrationScriptSource(assembly, resourceNamePrefix));
        return this;
    }
}
