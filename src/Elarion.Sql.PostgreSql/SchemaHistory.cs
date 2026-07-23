using Elarion.Migrations;
using Npgsql;

namespace Elarion.Sql.PostgreSql;

/// <summary>
/// PostgreSQL operations on the <c>elarion_schema_history</c> table over the session's dedicated
/// connection. The table is created by the runner itself under the advisory lock; rows for transactional
/// migrations are inserted inside the migration's own transaction (the no-repair invariant of ADR-0057).
/// Every statement names the table schema-qualified when the connection's search path selects one, so
/// history writes stay anchored even if a script leaves the session's search path pointing elsewhere.
/// </summary>
internal sealed class SchemaHistory(
    NpgsqlConnection connection,
    string? schema,
    string tableName,
    int commandTimeoutSeconds) {
    private readonly string _quotedTable =
        schema is null ? '"' + tableName + '"' : $"\"{schema}\".\"{tableName}\"";

    public async Task EnsureTableAsync(CancellationToken cancellationToken) {
        var sql = $"""
                   CREATE TABLE IF NOT EXISTS {_quotedTable} (
                       installed_rank integer NOT NULL PRIMARY KEY,
                       version text NULL,
                       description text NOT NULL,
                       script_name text NOT NULL,
                       checksum text NULL,
                       state text NOT NULL,
                       applied_at timestamptz NOT NULL DEFAULT now(),
                       duration_ms bigint NOT NULL
                   );
                   CREATE UNIQUE INDEX IF NOT EXISTS "{tableName}_version_key" ON {_quotedTable} (version) WHERE version IS NOT NULL;
                   """;
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TableExistsAsync(CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection) {
            CommandTimeout = commandTimeoutSeconds,
            Parameters = { new NpgsqlParameter<string> { TypedValue = _quotedTable } }
        };
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<AppliedMigrationRow>> LoadAsync(CancellationToken cancellationToken) {
        var sql =
            $"SELECT installed_rank, version, description, script_name, checksum, state FROM {_quotedTable} ORDER BY installed_rank";
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
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

    public async Task InsertAsync(MigrationHistoryRecord row, NpgsqlTransaction? transaction,
        CancellationToken cancellationToken) {
        var sql = $"""
                   INSERT INTO {_quotedTable} (installed_rank, version, description, script_name, checksum, state, duration_ms)
                   VALUES ($1, $2, $3, $4, $5, $6, $7)
                   """;
        await using var command = new NpgsqlCommand(sql, connection, transaction) {
            CommandTimeout = commandTimeoutSeconds,
            Parameters = {
                new NpgsqlParameter<int> { TypedValue = row.InstalledRank },
                new NpgsqlParameter<string?> { TypedValue = row.Version },
                new NpgsqlParameter<string> { TypedValue = row.Description },
                new NpgsqlParameter<string> { TypedValue = row.ScriptName },
                new NpgsqlParameter<string?> { TypedValue = row.Checksum },
                new NpgsqlParameter<string> { TypedValue = row.State },
                new NpgsqlParameter<long> { TypedValue = row.DurationMs }
            }
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(int installedRank, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand($"DELETE FROM {_quotedTable} WHERE installed_rank = $1", connection) {
            CommandTimeout = commandTimeoutSeconds,
            Parameters = { new NpgsqlParameter<int> { TypedValue = installedRank } }
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkAppliedAsync(int installedRank, string? checksum, CancellationToken cancellationToken) {
        var sql = $"UPDATE {_quotedTable} SET state = $2, checksum = COALESCE($3, checksum) WHERE installed_rank = $1";
        await using var command = new NpgsqlCommand(sql, connection) {
            CommandTimeout = commandTimeoutSeconds,
            Parameters = {
                new NpgsqlParameter<int> { TypedValue = installedRank },
                new NpgsqlParameter<string> { TypedValue = MigrationStates.Applied },
                new NpgsqlParameter<string?> { TypedValue = checksum }
            }
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
