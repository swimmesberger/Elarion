using Elarion.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Elarion.Migrations.PostgreSql;

/// <summary>
/// The PostgreSQL convenience façade over the neutral <see cref="MigrationRunner"/> (ADR-0057): construct
/// it from a connection string or an <see cref="NpgsqlDataSource"/> and it wires the
/// <see cref="PostgreSqlMigrationDatabase"/> for you. Behaviour is entirely the base engine's.
/// </summary>
public sealed class PostgreSqlMigrationRunner : MigrationRunner {
    /// <summary>Creates a runner that opens its dedicated connection from a connection string.</summary>
    public PostgreSqlMigrationRunner(string connectionString, PostgreSqlMigrationOptions options, ILogger<PostgreSqlMigrationRunner>? logger = null)
        : base(new PostgreSqlMigrationDatabase(connectionString, options, logger), options, logger) {
    }

    /// <summary>Creates a runner that borrows connections from an existing data source (never disposes it).</summary>
    public PostgreSqlMigrationRunner(NpgsqlDataSource dataSource, PostgreSqlMigrationOptions options, ILogger<PostgreSqlMigrationRunner>? logger = null)
        : base(new PostgreSqlMigrationDatabase(dataSource, options, logger), options, logger) {
    }
}
