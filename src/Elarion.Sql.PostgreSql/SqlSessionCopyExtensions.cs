using System.Data.Common;
using Npgsql;

namespace Elarion.Sql;

/// <summary>
/// PostgreSQL binary-COPY bulk insert for the AOT SQL tier (ADR-0068): streams
/// <see cref="INpgsqlCopyRecord{TSelf}"/> rows through <c>COPY … FROM STDIN (FORMAT BINARY)</c> on the
/// session's own connection, inside its ambient transaction. The default streams straight into the
/// target table; the upsert behaviors stage through a per-call temporary table and merge with
/// PostgreSQL's native <c>ON CONFLICT</c>, because COPY itself cannot express conflict handling.
/// </summary>
/// <remarks>
/// The verb and conflict vocabulary deliberately mirror the EF tier's <c>ExecuteInsertAsync</c>
/// (ADR-0051): set-based, non-tracking, no values fetched back. <c>InsertManyAsync</c> remains the
/// small-batch convenience for tens-to-hundreds of rows; this is the volume path.
/// </remarks>
/// <example>
/// <code>
/// long written = await session.ExecuteInsertAsync(rows, new SqlBulkInsertOptions {
///     OnConflict = SqlBulkInsertConflictBehavior.Update,
///     ConflictColumns = ["id"],
/// }, ct);
/// </code>
/// </example>
public static class SqlSessionCopyExtensions {
    private static readonly SqlBulkInsertOptions DefaultOptions = new();

    /// <summary>
    /// Bulk-inserts every row via binary COPY; returns the number of rows inserted (or, for the upsert
    /// behaviors, inserted or updated). An <see cref="IReadOnlyList{T}"/> input (a reused batch buffer)
    /// is written by index, without an enumerator.
    /// </summary>
    public static async Task<long> ExecuteInsertAsync<T>(
        this ISqlSession session, IEnumerable<T> rows, SqlBulkInsertOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : INpgsqlCopyRecord<T> {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows is ICollection<T> { Count: 0 } or IReadOnlyCollection<T> { Count: 0 }) return 0;

        options ??= DefaultOptions;
        Func<NpgsqlBinaryImporter, CancellationToken, Task> writeRows =
            (importer, ct) => WriteRowsAsync(importer, rows, ct);
        return await ExecuteAsync<T>(session, options, writeRows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bulk-inserts a streaming row sequence via binary COPY — rows are written as they are produced,
    /// nothing is materialized.
    /// </summary>
    public static async Task<long> ExecuteInsertAsync<T>(
        this ISqlSession session, IAsyncEnumerable<T> rows, SqlBulkInsertOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : INpgsqlCopyRecord<T> {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(rows);

        options ??= DefaultOptions;
        Func<NpgsqlBinaryImporter, CancellationToken, Task> writeRows =
            (importer, ct) => WriteRowsAsync(importer, rows, ct);
        return await ExecuteAsync<T>(session, options, writeRows, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ExecuteAsync<T>(
        ISqlSession session, SqlBulkInsertOptions options,
        Func<NpgsqlBinaryImporter, CancellationToken, Task> writeRows, CancellationToken cancellationToken)
        where T : INpgsqlCopyRecord<T> {
        // Misconfiguration fails loud before the database is touched.
        MergeSql? mergeSql = options.OnConflict == SqlBulkInsertConflictBehavior.Throw
            ? null
            : BuildMergeSql<T>(options);

        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is not NpgsqlConnection npgsql)
            throw new InvalidOperationException(
                "Elarion PostgreSQL bulk insert requires an Npgsql connection; the session's connection is not an NpgsqlConnection.");

        // Dapper semantics, like the session query/insert helpers: a closed connection is opened for
        // the call and closed afterwards; an already-open one (the scoped session's) is left open.
        var wasClosed = npgsql.State == System.Data.ConnectionState.Closed;
        if (wasClosed) await npgsql.OpenAsync(cancellationToken).ConfigureAwait(false);

        try {
            if (mergeSql is null)
                return await DirectInsertAsync<T>(npgsql, options, writeRows, cancellationToken)
                    .ConfigureAwait(false);

            return await StagedInsertAsync<T>(npgsql, session.CurrentTransaction, options, mergeSql.Value,
                writeRows, cancellationToken).ConfigureAwait(false);
        }
        finally {
            if (wasClosed) await npgsql.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task<long> DirectInsertAsync<T>(
        NpgsqlConnection connection, SqlBulkInsertOptions options,
        Func<NpgsqlBinaryImporter, CancellationToken, Task> writeRows, CancellationToken cancellationToken)
        where T : INpgsqlCopyRecord<T> {
        var importer = await connection.BeginBinaryImportAsync(T.CopyCommandText, cancellationToken)
            .ConfigureAwait(false);
        await using (importer.ConfigureAwait(false)) {
            if (options.Timeout is { } timeout) importer.Timeout = timeout;

            await writeRows(importer, cancellationToken).ConfigureAwait(false);
            return (long)await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> StagedInsertAsync<T>(
        NpgsqlConnection connection, DbTransaction? transaction, SqlBulkInsertOptions options,
        MergeSql mergeSql, Func<NpgsqlBinaryImporter, CancellationToken, Task> writeRows,
        CancellationToken cancellationToken)
        where T : INpgsqlCopyRecord<T> {
        // Random (not v7) suffix: concurrent calls on other connections may stage at the same instant,
        // and a v7 prefix is a timestamp — the randomness is the point here, not locality.
        var tempTable = "elarion_bulk_stage_" + Guid.NewGuid().ToString("N")[..8];

        // The staging table clones only the mapped columns' definitions — no constraints, no defaults —
        // so partial-width records COPY into it without tripping the target's NOT NULLs.
        await ExecuteStatementAsync(
            connection, transaction, options,
            $"CREATE TEMP TABLE {tempTable} AS SELECT {T.CopyColumnList} FROM {T.CopyTableName} WITH NO DATA",
            cancellationToken).ConfigureAwait(false);
        try {
            var importer = await connection
                .BeginBinaryImportAsync(
                    $"COPY {tempTable} ({T.CopyColumnList}) FROM STDIN (FORMAT BINARY)", cancellationToken)
                .ConfigureAwait(false);
            await using (importer.ConfigureAwait(false)) {
                if (options.Timeout is { } timeout) importer.Timeout = timeout;

                await writeRows(importer, cancellationToken).ConfigureAwait(false);
                await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            return await ExecuteStatementAsync(
                connection, transaction, options, mergeSql.ToStatement(tempTable), cancellationToken)
                .ConfigureAwait(false);
        }
        finally {
            // Best-effort: inside an aborted ambient transaction the DROP cannot run, but the temp
            // table dies with the rollback / session in that case anyway.
            try {
                await ExecuteStatementAsync(
                    connection, transaction, options, $"DROP TABLE IF EXISTS {tempTable}",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) {
                // Intentionally swallowed — cleanup only; the original failure is the signal.
            }
        }
    }

    /// <summary>The merge statement with the staging-table name left open (it is minted per call).</summary>
    private readonly record struct MergeSql(string Prefix, string Suffix) {
        public string ToStatement(string tempTable) => Prefix + tempTable + Suffix;
    }

    private static MergeSql BuildMergeSql<T>(SqlBulkInsertOptions options)
        where T : INpgsqlCopyRecord<T> {
        var columns = T.CopyColumnList.Split(", ", StringSplitOptions.None);

        var conflictColumns = options.ConflictColumns is { Count: > 0 } named ? named : null;
        if (conflictColumns is null && options.OnConflict == SqlBulkInsertConflictBehavior.Update)
            throw new InvalidOperationException(
                $"Bulk upsert into '{T.CopyTableName}' needs SqlBulkInsertOptions.ConflictColumns: this tier has "
                + "no key metadata, so the ON CONFLICT target cannot be inferred.");

        var conflictTarget = "";
        if (conflictColumns is not null) {
            foreach (var name in conflictColumns)
                if (Array.IndexOf(columns, name) < 0)
                    throw new InvalidOperationException(
                        $"Conflict column '{name}' is not a mapped column of '{T.CopyTableName}' "
                        + $"(mapped: {T.CopyColumnList}).");

            conflictTarget = " (" + string.Join(", ", conflictColumns) + ")";
        }

        var prefix = $"INSERT INTO {T.CopyTableName} ({T.CopyColumnList}) SELECT {T.CopyColumnList} FROM ";
        if (options.OnConflict == SqlBulkInsertConflictBehavior.DoNothing)
            return new MergeSql(prefix, $" ON CONFLICT{conflictTarget} DO NOTHING");

        var assignments = columns
            .Where(column => !conflictColumns!.Contains(column, StringComparer.Ordinal))
            .Select(column => $"{column} = EXCLUDED.{column}")
            .ToList();
        if (assignments.Count == 0)
            throw new InvalidOperationException(
                $"Every mapped column of '{T.CopyTableName}' is part of the conflict target; there is "
                + "nothing to update — use SqlBulkInsertConflictBehavior.DoNothing.");

        return new MergeSql(prefix, $" ON CONFLICT{conflictTarget} DO UPDATE SET {string.Join(", ", assignments)}");
    }

    private static async Task<long> ExecuteStatementAsync(
        NpgsqlConnection connection, DbTransaction? transaction, SqlBulkInsertOptions options, string sql,
        CancellationToken cancellationToken) {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false)) {
            command.CommandText = sql;
            command.Transaction = (NpgsqlTransaction?)transaction;
            if (options.Timeout is { } timeout) command.CommandTimeout = Math.Max(1, (int)timeout.TotalSeconds);

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteRowsAsync<T>(
        NpgsqlBinaryImporter importer, IEnumerable<T> rows, CancellationToken cancellationToken)
        where T : INpgsqlCopyRecord<T> {
        // A reused flush buffer (List<T>) writes by index — no boxed enumerator on the batch path.
        if (rows is IReadOnlyList<T> list) {
            for (var i = 0; i < list.Count; i++)
                await T.WriteRowAsync(importer, list[i], cancellationToken).ConfigureAwait(false);

            return;
        }

        foreach (var row in rows)
            await T.WriteRowAsync(importer, row, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRowsAsync<T>(
        NpgsqlBinaryImporter importer, IAsyncEnumerable<T> rows, CancellationToken cancellationToken)
        where T : INpgsqlCopyRecord<T> {
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            await T.WriteRowAsync(importer, row, cancellationToken).ConfigureAwait(false);
    }
}
