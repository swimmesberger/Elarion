using Elarion.Migrations;
using Microsoft.Extensions.Logging;

namespace Elarion.Migrations.Sqlite;

/// <summary>
/// The SQLite <see cref="IMigrationDatabaseFactory"/> that <c>AddElarionSqlite</c> registers: it captures the
/// connection string chosen at provider registration and builds a <see cref="SqliteMigrationDatabase"/> over the
/// neutral options the neutral <c>AddElarionMigrations</c> passes in — the SQLite counterpart to
/// PostgreSQL's factory, so migration wiring stays database-neutral.
/// </summary>
internal sealed class SqliteMigrationDatabaseFactory(string connectionString) : IMigrationDatabaseFactory {
    public IMigrationDatabase Create(MigrationOptions options, ILogger? logger) {
        return new SqliteMigrationDatabase(connectionString, options);
    }
}
