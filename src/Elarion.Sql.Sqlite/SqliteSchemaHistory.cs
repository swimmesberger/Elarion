using System.Data.Common;
using Elarion.Migrations;
using Microsoft.Data.Sqlite;

namespace Elarion.Sql.Sqlite;

/// <summary>
/// SQLite operations on the <c>elarion_schema_history</c> table over the session's dedicated connection
/// (ADR-0060). SQLite has full transactional DDL, so a transactional migration's history row commits with
/// its step exactly as on PostgreSQL — the roll-forward, no-repair invariant holds unchanged. A table written
/// by the script-only runner is upgraded in place on the first exclusive run and read through column aliases
/// until then.
/// </summary>
internal sealed class SqliteSchemaHistory(SqliteConnection connection, string tableName, int commandTimeoutSeconds) {
    private readonly string _quotedTable = '"' + tableName + '"';

    public async Task EnsureTableAsync(CancellationToken cancellationToken) {
        var sql = $"""
                   CREATE TABLE IF NOT EXISTS {_quotedTable} (
                       installed_rank INTEGER NOT NULL PRIMARY KEY,
                       kind TEXT NOT NULL,
                       version TEXT NULL,
                       description TEXT NOT NULL,
                       step_name TEXT NOT NULL,
                       checksum TEXT NULL,
                       outcome TEXT NOT NULL,
                       applied_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                       duration_ms INTEGER NOT NULL
                   );
                   CREATE UNIQUE INDEX IF NOT EXISTS "{tableName}_version_key" ON {_quotedTable} (version) WHERE version IS NOT NULL;
                   """;
        await using (var command = NewCommand(sql)) {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await IsLegacyLayoutAsync(cancellationToken)) return;

        // The pre-ADR-0081 layout was script-shaped (script_name, state). Rename in place and tag the rows
        // as SQL steps; baseline markers get their own kind.
        var upgrade = $"""
                       ALTER TABLE {_quotedTable} RENAME COLUMN script_name TO step_name;
                       ALTER TABLE {_quotedTable} RENAME COLUMN state TO outcome;
                       ALTER TABLE {_quotedTable} ADD COLUMN kind TEXT NOT NULL DEFAULT 'sql';
                       UPDATE {_quotedTable} SET kind = 'baseline' WHERE outcome = 'baseline';
                       """;
        await using var upgradeCommand = NewCommand(upgrade);
        await upgradeCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> IsLegacyLayoutAsync(CancellationToken cancellationToken) {
        await using var command = NewCommand(
            "SELECT count(*) FROM pragma_table_info($name) WHERE name = 'script_name'");
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    public async Task<bool> TableExistsAsync(CancellationToken cancellationToken) {
        await using var command =
            NewCommand("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name");
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    public async Task<IReadOnlyList<AppliedMigrationRow>> LoadAsync(CancellationToken cancellationToken) {
        // A read-only session may meet the legacy layout before any exclusive run upgraded it: alias the old
        // columns instead of writing.
        var sql = await IsLegacyLayoutAsync(cancellationToken)
            ? $"""
               SELECT installed_rank, CASE WHEN state = 'baseline' THEN 'baseline' ELSE 'sql' END, version, description, script_name, checksum, state
               FROM {_quotedTable} ORDER BY installed_rank
               """
            : $"SELECT installed_rank, kind, version, description, step_name, checksum, outcome FROM {_quotedTable} ORDER BY installed_rank";
        await using var command = NewCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<AppliedMigrationRow>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new AppliedMigrationRow {
                InstalledRank = reader.GetInt32(0),
                Kind = reader.GetString(1),
                Version = reader.IsDBNull(2) ? null : reader.GetString(2),
                Description = reader.GetString(3),
                StepName = reader.GetString(4),
                Checksum = reader.IsDBNull(5) ? null : reader.GetString(5),
                Outcome = reader.GetString(6)
            });

        return rows;
    }

    public async Task InsertAsync(MigrationHistoryRecord row, DbTransaction? transaction,
        CancellationToken cancellationToken) {
        var sql = $"""
                   INSERT INTO {_quotedTable} (installed_rank, kind, version, description, step_name, checksum, outcome, duration_ms)
                   VALUES ($installed_rank, $kind, $version, $description, $step_name, $checksum, $outcome, $duration_ms)
                   """;
        await using var command = NewCommand(sql, transaction);
        command.Parameters.AddWithValue("$installed_rank", row.InstalledRank);
        command.Parameters.AddWithValue("$kind", row.Kind);
        command.Parameters.AddWithValue("$version", (object?)row.Version ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", row.Description);
        command.Parameters.AddWithValue("$step_name", row.StepName);
        command.Parameters.AddWithValue("$checksum", (object?)row.Checksum ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", row.Outcome);
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
            $"UPDATE {_quotedTable} SET outcome = $outcome, checksum = COALESCE($checksum, checksum) WHERE installed_rank = $installed_rank";
        await using var command = NewCommand(sql);
        command.Parameters.AddWithValue("$installed_rank", installedRank);
        command.Parameters.AddWithValue("$outcome", MigrationOutcomes.Applied);
        command.Parameters.AddWithValue("$checksum", (object?)checksum ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteCommand NewCommand(string sql, DbTransaction? transaction = null) {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Transaction = (SqliteTransaction?)transaction;
        return command;
    }
}
