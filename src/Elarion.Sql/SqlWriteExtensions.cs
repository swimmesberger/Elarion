using System.Data;
using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// Internal write plumbing (ADR-0058): full-row inserts from the generated <c>InsertCommandText</c> + typed
/// <c>BindParameters</c>, behind the single public surface (<see cref="SqlSessionExtensions"/>), which passes
/// the session's ambient transaction through. <c>InsertManyAsync</c> is a batched convenience (one prepared
/// statement reused per row inside one transaction), not bulk COPY — for high-throughput bulk load use the
/// binary-COPY path (<c>Elarion.BulkOperations.PostgreSql</c>, ADR-0051). Not public: the per-call
/// <see cref="DbTransaction"/> parameter is exactly the forget-to-thread-it footgun the session surface
/// exists to remove.
/// </summary>
internal static class SqlWriteExtensions {
    /// <summary>Inserts one row via the generated full-row INSERT; returns the affected row count.</summary>
    public static async Task<int> InsertAsync<T>(
        this DbConnection connection, T row, DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false)) {
                if (transaction is not null) command.Transaction = transaction;

                command.CommandText = T.InsertCommandText;
                T.SqlMapper.BindParameters(command, row);
                return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally {
            if (wasClosed) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Inserts every row via the generated full-row INSERT, reusing one prepared command inside one
    /// transaction (its own if none is passed). <paramref name="sqlSuffix"/> is appended verbatim to
    /// the INSERT (e.g. <c>" ON CONFLICT DO NOTHING"</c>). Returns the total affected row count.
    /// </summary>
    public static async Task<int> InsertManyAsync<T>(
        this DbConnection connection, IEnumerable<T> rows, string? sqlSuffix = null,
        DbTransaction? transaction = null, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        ArgumentNullException.ThrowIfNull(rows);
        var mapper = T.SqlMapper;
        var commandText = sqlSuffix is null ? T.InsertCommandText : T.InsertCommandText + sqlSuffix;

        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var ownsTransaction = transaction is null;
        var tx = transaction
                 ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false)) {
                command.Transaction = tx;
                command.CommandText = commandText;
                var written = 0;
                foreach (var row in rows) {
                    command.Parameters.Clear();
                    mapper.BindParameters(command, row);
                    written += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                if (ownsTransaction) await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

                return written;
            }
        }
        catch {
            if (ownsTransaction) await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);

            throw;
        }
        finally {
            if (ownsTransaction) await tx.DisposeAsync().ConfigureAwait(false);

            if (wasClosed) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Executes a non-query statement in the given transaction; returns the affected row count.</summary>
    public static async Task<int> ExecuteAsync(
        this DbConnection connection, SqlStatement sql, DbTransaction transaction,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(transaction);
        var command = sql.CreateCommand(connection);
        await using (command.ConfigureAwait(false)) {
            command.Transaction = transaction;
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="ExecuteAsync(DbConnection, SqlStatement, DbTransaction, CancellationToken)"/>
    public static Task<int> ExecuteAsync(
        this DbConnection connection, SqlInterpolatedStringHandler sql, DbTransaction transaction,
        CancellationToken cancellationToken = default) {
        return connection.ExecuteAsync(new SqlStatement(sql), transaction, cancellationToken);
    }
}
