using System.Data.Common;
using Elarion.Migrations;
using Npgsql;

namespace Elarion.Sql.PostgreSql;

/// <summary>
/// PostgreSQL operations on the <c>elarion_schema_history</c> table over the session's dedicated
/// connection. The table is created by the runner itself under the advisory lock; rows for transactional
/// steps are inserted inside the step's own transaction (the no-repair invariant of ADR-0057, ADR-0081). A
/// table written by the script-only runner is upgraded in place on the first exclusive run and read through
/// column aliases until then.
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
                       kind text NOT NULL,
                       version text NULL,
                       description text NOT NULL,
                       step_name text NOT NULL,
                       checksum text NULL,
                       outcome text NOT NULL,
                       applied_at timestamptz NOT NULL DEFAULT now(),
                       duration_ms bigint NOT NULL
                   );
                   CREATE UNIQUE INDEX IF NOT EXISTS "{tableName}_version_key" ON {_quotedTable} (version) WHERE version IS NOT NULL;
                   """;
        await using (var command = new NpgsqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds }) {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await IsLegacyLayoutAsync(cancellationToken)) return;

        // The pre-ADR-0081 layout was script-shaped (script_name, state). Rename in place — the rows keep
        // meaning — and tag them as SQL steps; baseline markers get their own kind.
        var upgrade = $"""
                       ALTER TABLE {_quotedTable} RENAME COLUMN script_name TO step_name;
                       ALTER TABLE {_quotedTable} RENAME COLUMN state TO outcome;
                       ALTER TABLE {_quotedTable} ADD COLUMN kind text NOT NULL DEFAULT 'sql';
                       ALTER TABLE {_quotedTable} ALTER COLUMN kind DROP DEFAULT;
                       UPDATE {_quotedTable} SET kind = 'baseline' WHERE outcome = 'baseline';
                       """;
        await using var upgradeCommand = new NpgsqlCommand(upgrade, connection)
            { CommandTimeout = commandTimeoutSeconds };
        await upgradeCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> IsLegacyLayoutAsync(CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($1) AND attname = 'script_name' AND NOT attisdropped)",
            connection) {
            CommandTimeout = commandTimeoutSeconds,
            Parameters = { new NpgsqlParameter<string> { TypedValue = _quotedTable } }
        };
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> TableExistsAsync(CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection) {
            CommandTimeout = commandTimeoutSeconds,
            Parameters = { new NpgsqlParameter<string> { TypedValue = _quotedTable } }
        };
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
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
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
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
                   VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                   """;
        await using var command = new NpgsqlCommand(sql, connection, (NpgsqlTransaction?)transaction) {
            CommandTimeout = commandTimeoutSeconds,
            Parameters = {
                new NpgsqlParameter<int> { TypedValue = row.InstalledRank },
                new NpgsqlParameter<string> { TypedValue = row.Kind },
                new NpgsqlParameter<string?> { TypedValue = row.Version },
                new NpgsqlParameter<string> { TypedValue = row.Description },
                new NpgsqlParameter<string> { TypedValue = row.StepName },
                new NpgsqlParameter<string?> { TypedValue = row.Checksum },
                new NpgsqlParameter<string> { TypedValue = row.Outcome },
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
        var sql = $"UPDATE {_quotedTable} SET outcome = $2, checksum = COALESCE($3, checksum) WHERE installed_rank = $1";
        await using var command = new NpgsqlCommand(sql, connection) {
            CommandTimeout = commandTimeoutSeconds,
            Parameters = {
                new NpgsqlParameter<int> { TypedValue = installedRank },
                new NpgsqlParameter<string> { TypedValue = MigrationOutcomes.Applied },
                new NpgsqlParameter<string?> { TypedValue = checksum }
            }
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
