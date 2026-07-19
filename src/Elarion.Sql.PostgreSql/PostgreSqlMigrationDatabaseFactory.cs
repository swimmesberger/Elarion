using Elarion.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Elarion.Sql.PostgreSql;

/// <summary>
/// The PostgreSQL <see cref="IMigrationDatabaseFactory"/> that <c>AddElarionPostgreSql</c> registers: it captures
/// the central <see cref="NpgsqlDataSource"/> and the advisory-lock key chosen at provider registration, and
/// builds a <see cref="PostgreSqlMigrationDatabase"/> over the neutral options the neutral
/// <c>AddElarionMigrations</c> passes in — so migration wiring stays database-neutral and shares the one data
/// source with the access tier.
/// </summary>
internal sealed class PostgreSqlMigrationDatabaseFactory(NpgsqlDataSource dataSource, long advisoryLockKey)
    : IMigrationDatabaseFactory {
    public IMigrationDatabase Create(MigrationOptions options, ILogger? logger) {
        return new PostgreSqlMigrationDatabase(dataSource, options, advisoryLockKey, logger);
    }
}
