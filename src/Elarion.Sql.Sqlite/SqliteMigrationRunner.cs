using Elarion.Migrations;
using Microsoft.Extensions.Logging;

namespace Elarion.Sql.Sqlite;

/// <summary>
/// The SQLite convenience façade over the neutral <see cref="MigrationRunner"/> (ADR-0060): construct it
/// from a connection string (<c>Data Source=app.db</c>) and it wires the
/// <see cref="SqliteMigrationDatabase"/> for you. Behaviour is entirely the base engine's. Use a
/// file-based database — a dedicated <c>:memory:</c> connection is discarded when the runner closes it. The
/// normal DI path is <c>AddElarionSqlite</c> + <c>AddElarionMigrations</c>; this façade is for direct/non-DI use.
/// </summary>
public sealed class SqliteMigrationRunner : MigrationRunner {
    /// <summary>Creates a runner that opens its dedicated connection from a SQLite connection string.</summary>
    public SqliteMigrationRunner(string connectionString, MigrationOptions options,
        ILogger<SqliteMigrationRunner>? logger = null)
        : base(new SqliteMigrationDatabase(connectionString, options), options, logger) {
    }
}
