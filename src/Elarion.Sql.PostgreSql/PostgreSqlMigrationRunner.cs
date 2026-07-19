using Elarion.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Elarion.Sql.PostgreSql;

/// <summary>
/// The PostgreSQL convenience façade over the neutral <see cref="MigrationRunner"/> (ADR-0057): construct
/// it from a connection string or an <see cref="NpgsqlDataSource"/> and it wires the PostgreSQL
/// <see cref="IMigrationDatabase"/> for you. Behaviour is entirely the base engine's. The normal DI path is
/// <c>AddElarionPostgreSql</c> + <c>AddElarionMigrations</c>; this façade is for direct/non-DI construction.
/// </summary>
public sealed class PostgreSqlMigrationRunner : MigrationRunner {
    /// <summary>
    /// The default session-level advisory lock key — the first eight bytes (big-endian) of
    /// SHA-256("Elarion.Migrations.PostgreSql"). Two applications migrating independent schemas in one
    /// database can pick distinct keys to migrate concurrently.
    /// </summary>
    public const long DefaultAdvisoryLockKey = -6165385607603977853;

    /// <summary>Creates a runner that opens its dedicated connection from a connection string.</summary>
    public PostgreSqlMigrationRunner(string connectionString, MigrationOptions options,
        long advisoryLockKey = DefaultAdvisoryLockKey, ILogger<PostgreSqlMigrationRunner>? logger = null)
        : base(new PostgreSqlMigrationDatabase(connectionString, options, advisoryLockKey, logger), options, logger) {
    }

    /// <summary>Creates a runner that borrows connections from an existing data source (never disposes it).</summary>
    public PostgreSqlMigrationRunner(NpgsqlDataSource dataSource, MigrationOptions options,
        long advisoryLockKey = DefaultAdvisoryLockKey, ILogger<PostgreSqlMigrationRunner>? logger = null)
        : base(new PostgreSqlMigrationDatabase(dataSource, options, advisoryLockKey, logger), options, logger) {
    }
}
