using System.Reflection;

namespace Elarion.Migrations.PostgreSql;

/// <summary>One assembly whose embedded resources are scanned for migration scripts.</summary>
/// <param name="Assembly">The assembly carrying the embedded <c>.sql</c> resources.</param>
/// <param name="ResourceNamePrefix">
/// Optional ordinal prefix limiting the scan (e.g. <c>"MyApp.Migrations."</c>). Without it every
/// embedded <c>.sql</c> resource of the assembly must be a valid migration script.
/// </param>
public sealed record MigrationScriptSource(Assembly Assembly, string? ResourceNamePrefix);

/// <summary>Options for the PostgreSQL migration runner (ADR-0057).</summary>
public sealed class PostgreSqlMigrationOptions {
    /// <summary>
    /// The default session-level advisory lock key — the first eight bytes (big-endian) of
    /// SHA-256("Elarion.Migrations.PostgreSql"). Two applications migrating independent schemas in one
    /// database can pick distinct keys to migrate concurrently.
    /// </summary>
    public const long DefaultAdvisoryLockKey = -6165385607603977853;

    private readonly List<MigrationScriptSource> _scriptSources = [];

    /// <summary>The assemblies scanned for embedded migration scripts, in registration order.</summary>
    public IReadOnlyList<MigrationScriptSource> ScriptSources => _scriptSources;

    /// <summary>
    /// The history table the runner creates and maintains. A plain identifier (created in the
    /// connection's default schema); defaults to <c>elarion_schema_history</c>.
    /// </summary>
    public string HistoryTableName { get; set; } = "elarion_schema_history";

    /// <summary>
    /// What to do with a pending script versioned below an already-applied one. Defaults to
    /// <see cref="OutOfOrderPolicy.Warn"/>: apply, warn, record in true execution order.
    /// </summary>
    public OutOfOrderPolicy OutOfOrder { get; set; } = OutOfOrderPolicy.Warn;

    /// <summary>The session-level advisory lock key serializing concurrent runners. Defaults to <see cref="DefaultAdvisoryLockKey"/>.</summary>
    public long AdvisoryLockKey { get; set; } = DefaultAdvisoryLockKey;

    /// <summary>
    /// The per-command timeout for executing scripts and history statements. Defaults to
    /// <see langword="null"/> — no timeout — because long-running DDL is normal for migrations
    /// (deliberately not Npgsql's 30-second default).
    /// </summary>
    public TimeSpan? CommandTimeout { get; set; }

    /// <summary>
    /// How long to wait for the advisory lock when another runner holds it. Defaults to
    /// <see langword="null"/> — wait indefinitely — so concurrent startups serialize behind a long
    /// migration instead of failing.
    /// </summary>
    public TimeSpan? LockTimeout { get; set; }

    /// <summary>
    /// Whether <c>AddElarionPostgreSqlMigrations</c> also registers the hosted service that runs
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
    public PostgreSqlMigrationOptions AddScripts(Assembly assembly, string? resourceNamePrefix = null) {
        ArgumentNullException.ThrowIfNull(assembly);
        _scriptSources.Add(new MigrationScriptSource(assembly, resourceNamePrefix));
        return this;
    }
}
