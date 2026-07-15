using Elarion.Migrations;

namespace Elarion.Migrations.PostgreSql;

/// <summary>Options for the PostgreSQL migration provider (ADR-0057), extending the neutral <see cref="MigrationOptions"/>.</summary>
public sealed class PostgreSqlMigrationOptions : MigrationOptions {
    /// <summary>
    /// The default session-level advisory lock key — the first eight bytes (big-endian) of
    /// SHA-256("Elarion.Migrations.PostgreSql"). Two applications migrating independent schemas in one
    /// database can pick distinct keys to migrate concurrently.
    /// </summary>
    public const long DefaultAdvisoryLockKey = -6165385607603977853;

    /// <summary>The session-level advisory lock key serializing concurrent runners. Defaults to <see cref="DefaultAdvisoryLockKey"/>.</summary>
    public long AdvisoryLockKey { get; set; } = DefaultAdvisoryLockKey;
}
