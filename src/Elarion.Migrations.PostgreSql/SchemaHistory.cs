using Npgsql;

namespace Elarion.Migrations.PostgreSql;

/// <summary>The wire values of the history table's <c>state</c> column.</summary>
internal static class MigrationStates {
    public const string Applied = "applied";
    public const string Failed = "failed";
    public const string Baseline = "baseline";
}

/// <summary>One row of the schema history table.</summary>
internal sealed record AppliedMigrationRow {
    public required int InstalledRank { get; init; }

    public required string? Version { get; init; }

    public required string Description { get; init; }

    public required string ScriptName { get; init; }

    public required string? Checksum { get; init; }

    public required string State { get; init; }
}

/// <summary>
/// Operations on the <c>elarion_schema_history</c> table over the runner's dedicated connection. The
/// table is created by the runner itself under the advisory lock; rows for transactional migrations are
/// inserted inside the migration's own transaction (the no-repair invariant of ADR-0057).
/// </summary>
internal sealed class SchemaHistory(NpgsqlConnection connection, string tableName, int commandTimeoutSeconds) {
    private readonly string _quotedTable = '"' + tableName + '"';

    /// <summary>History table names are plain identifiers; anything else fails before touching the database.</summary>
    public static void ValidateTableName(string tableName) {
        var valid = tableName.Length > 0
            && (char.IsAsciiLetter(tableName[0]) || tableName[0] == '_')
            && tableName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        if (!valid) {
            throw new MigrationException(
                $"History table name '{tableName}' is not a plain identifier (letters, digits, underscores, not starting with a digit).");
        }
    }

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
            Parameters = { new NpgsqlParameter<string> { TypedValue = _quotedTable } },
        };
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<List<AppliedMigrationRow>> LoadAsync(CancellationToken cancellationToken) {
        var sql = $"SELECT installed_rank, version, description, script_name, checksum, state FROM {_quotedTable} ORDER BY installed_rank";
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<AppliedMigrationRow>();
        while (await reader.ReadAsync(cancellationToken)) {
            rows.Add(new AppliedMigrationRow {
                InstalledRank = reader.GetInt32(0),
                Version = reader.IsDBNull(1) ? null : reader.GetString(1),
                Description = reader.GetString(2),
                ScriptName = reader.GetString(3),
                Checksum = reader.IsDBNull(4) ? null : reader.GetString(4),
                State = reader.GetString(5),
            });
        }

        return rows;
    }

    public async Task InsertAsync(
        int installedRank,
        string? version,
        string description,
        string scriptName,
        string? checksum,
        string state,
        long durationMs,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken) {
        var sql = $"""
            INSERT INTO {_quotedTable} (installed_rank, version, description, script_name, checksum, state, duration_ms)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction) {
            CommandTimeout = commandTimeoutSeconds,
            Parameters = {
                new NpgsqlParameter<int> { TypedValue = installedRank },
                new NpgsqlParameter<string?> { TypedValue = version },
                new NpgsqlParameter<string> { TypedValue = description },
                new NpgsqlParameter<string> { TypedValue = scriptName },
                new NpgsqlParameter<string?> { TypedValue = checksum },
                new NpgsqlParameter<string> { TypedValue = state },
                new NpgsqlParameter<long> { TypedValue = durationMs },
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(int installedRank, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand($"DELETE FROM {_quotedTable} WHERE installed_rank = $1", connection) {
            CommandTimeout = commandTimeoutSeconds,
            Parameters = { new NpgsqlParameter<int> { TypedValue = installedRank } },
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
                new NpgsqlParameter<string?> { TypedValue = checksum },
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
