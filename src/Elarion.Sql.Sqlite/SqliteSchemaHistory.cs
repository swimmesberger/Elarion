using Elarion.Migrations;
using Microsoft.Data.Sqlite;

namespace Elarion.Sql.Sqlite;

/// <summary>
/// SQLite operations on the <c>elarion_schema_history</c> table over the session's dedicated connection
/// (ADR-0060). SQLite has full transactional DDL, so a transactional migration's history row commits with
/// its script exactly as on PostgreSQL — the roll-forward, no-repair invariant holds unchanged.
/// </summary>
internal sealed class SqliteSchemaHistory(SqliteConnection connection, string tableName, int commandTimeoutSeconds) {
    private readonly string _quotedTable = '"' + tableName + '"';

    public async Task EnsureTableAsync(CancellationToken cancellationToken) {
        var sql = $"""
                   CREATE TABLE IF NOT EXISTS {_quotedTable} (
                       installed_rank INTEGER NOT NULL PRIMARY KEY,
                       version TEXT NULL,
                       description TEXT NOT NULL,
                       script_name TEXT NOT NULL,
                       checksum TEXT NULL,
                       state TEXT NOT NULL,
                       applied_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                       duration_ms INTEGER NOT NULL
                   );
                   CREATE UNIQUE INDEX IF NOT EXISTS "{tableName}_version_key" ON {_quotedTable} (version) WHERE version IS NOT NULL;
                   """;
        await using var command = NewCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TableExistsAsync(CancellationToken cancellationToken) {
        await using var command =
            NewCommand("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name");
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    public async Task<IReadOnlyList<AppliedMigrationRow>> LoadAsync(CancellationToken cancellationToken) {
        var sql =
            $"SELECT installed_rank, version, description, script_name, checksum, state FROM {_quotedTable} ORDER BY installed_rank";
        await using var command = NewCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<AppliedMigrationRow>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new AppliedMigrationRow {
                InstalledRank = reader.GetInt32(0),
                Version = reader.IsDBNull(1) ? null : reader.GetString(1),
                Description = reader.GetString(2),
                ScriptName = reader.GetString(3),
                Checksum = reader.IsDBNull(4) ? null : reader.GetString(4),
                State = reader.GetString(5)
            });

        return rows;
    }

    public async Task InsertAsync(MigrationHistoryRecord row, SqliteTransaction? transaction,
        CancellationToken cancellationToken) {
        var sql = $"""
                   INSERT INTO {_quotedTable} (installed_rank, version, description, script_name, checksum, state, duration_ms)
                   VALUES ($installed_rank, $version, $description, $script_name, $checksum, $state, $duration_ms)
                   """;
        await using var command = NewCommand(sql, transaction);
        command.Parameters.AddWithValue("$installed_rank", row.InstalledRank);
        command.Parameters.AddWithValue("$version", (object?)row.Version ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", row.Description);
        command.Parameters.AddWithValue("$script_name", row.ScriptName);
        command.Parameters.AddWithValue("$checksum", (object?)row.Checksum ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", row.State);
        command.Parameters.AddWithValue("$duration_ms", row.DurationMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(int installedRank, CancellationToken cancellationToken) {
        await using var command = NewCommand($"DELETE FROM {_quotedTable} WHERE installed_rank = $installed_rank");
        command.Parameters.AddWithValue("$installed_rank", installedRank);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkAppliedAsync(int installedRank, string? checksum, CancellationToken cancellationToken) {
        var sql =
            $"UPDATE {_quotedTable} SET state = $state, checksum = COALESCE($checksum, checksum) WHERE installed_rank = $installed_rank";
        await using var command = NewCommand(sql);
        command.Parameters.AddWithValue("$installed_rank", installedRank);
        command.Parameters.AddWithValue("$state", MigrationStates.Applied);
        command.Parameters.AddWithValue("$checksum", (object?)checksum ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteCommand NewCommand(string sql, SqliteTransaction? transaction = null) {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Transaction = transaction;
        return command;
    }
}
