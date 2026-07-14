using System.Data;
using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// Query/execute convenience over any <see cref="DbConnection"/>, pairing a <see cref="SqlStatement"/>
/// statement with a generated <see cref="ISqlRowMapper{T}"/>. Deliberately thin: no ORM, no statement
/// generation — these save the command/reader ceremony, nothing more. A closed connection is opened
/// for the call and closed afterwards (Dapper semantics); an open connection is left untouched.
/// </summary>
public static class SqlDbConnectionExtensions {
    /// <summary>Runs the query and materializes every row through <paramref name="mapper"/>.</summary>
    public static Task<List<T>> QueryAsync<T>(
        this DbConnection connection, ISqlRowMapper<T> mapper, SqlInterpolatedStringHandler sql,
        CancellationToken cancellationToken = default) =>
        connection.QueryAsync(mapper, SqlStatement.Of(sql), cancellationToken);

    /// <inheritdoc cref="QueryAsync{T}(DbConnection, ISqlRowMapper{T}, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<List<T>> QueryAsync<T>(
        this DbConnection connection, ISqlRowMapper<T> mapper, SqlStatement sql,
        CancellationToken cancellationToken = default) {
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try {
            var command = sql.CreateCommand(connection);
            await using (command.ConfigureAwait(false)) {
                var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken)
                    .ConfigureAwait(false);
                await using (reader.ConfigureAwait(false)) {
                    return await mapper.ReadAllAsync(reader, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally {
            if (wasClosed) {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs the query and maps the first row, or returns <see langword="default"/> when empty.</summary>
    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this DbConnection connection, ISqlRowMapper<T> mapper, SqlInterpolatedStringHandler sql,
        CancellationToken cancellationToken = default) =>
        connection.QueryFirstOrDefaultAsync(mapper, SqlStatement.Of(sql), cancellationToken);

    /// <inheritdoc cref="QueryFirstOrDefaultAsync{T}(DbConnection, ISqlRowMapper{T}, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<T?> QueryFirstOrDefaultAsync<T>(
        this DbConnection connection, ISqlRowMapper<T> mapper, SqlStatement sql,
        CancellationToken cancellationToken = default) {
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try {
            var command = sql.CreateCommand(connection);
            await using (command.ConfigureAwait(false)) {
                var reader = await command
                    .ExecuteReaderAsync(CommandBehavior.SingleResult | CommandBehavior.SingleRow, cancellationToken)
                    .ConfigureAwait(false);
                await using (reader.ConfigureAwait(false)) {
                    return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                        ? mapper.Read(reader)
                        : default;
                }
            }
        }
        finally {
            if (wasClosed) {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Executes a non-query statement and returns the affected row count.</summary>
    public static Task<int> ExecuteAsync(
        this DbConnection connection, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default) =>
        connection.ExecuteAsync(SqlStatement.Of(sql), cancellationToken);

    /// <inheritdoc cref="ExecuteAsync(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<int> ExecuteAsync(
        this DbConnection connection, SqlStatement sql, CancellationToken cancellationToken = default) {
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try {
            var command = sql.CreateCommand(connection);
            await using (command.ConfigureAwait(false)) {
                return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally {
            if (wasClosed) {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Executes the statement and returns the first column of the first row, or
    /// <see langword="default"/> when the result is empty or <see cref="DBNull"/>.
    /// </summary>
    public static Task<TResult?> ExecuteScalarAsync<TResult>(
        this DbConnection connection, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default) =>
        connection.ExecuteScalarAsync<TResult>(SqlStatement.Of(sql), cancellationToken);

    /// <inheritdoc cref="ExecuteScalarAsync{TResult}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<TResult?> ExecuteScalarAsync<TResult>(
        this DbConnection connection, SqlStatement sql, CancellationToken cancellationToken = default) {
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try {
            var command = sql.CreateCommand(connection);
            await using (command.ConfigureAwait(false)) {
                var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return value is TResult result ? result : default;
            }
        }
        finally {
            if (wasClosed) {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
