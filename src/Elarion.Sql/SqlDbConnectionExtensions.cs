using System.Data;
using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// Query/execute convenience over a <see cref="DbDataSource"/> or <see cref="DbConnection"/>. The
/// self-mapping happy path resolves the generated mapper from the row type
/// (<c>connection.QueryAsync&lt;Order&gt;($"…")</c>, no mapper argument — see
/// <see cref="ISqlRecord{TSelf}"/>); an explicit-mapper escape hatch stays for hand-written mappers of
/// non-<c>[SqlRecord]</c> shapes. Deliberately thin: no ORM, no statement generation — these save the
/// command/reader ceremony, nothing more. On a <see cref="DbConnection"/>, a closed connection is
/// opened for the call and closed afterwards (Dapper semantics); on a <see cref="DbDataSource"/>, a
/// pooled connection is opened and disposed per call.
/// </summary>
public static class SqlDbConnectionExtensions {
    // ---- Self-mapping happy path: DbConnection ----------------------------------------------------

    /// <summary>Runs the query and materializes every row through the row type's generated mapper.</summary>
    public static Task<List<T>> QueryAsync<T>(
        this DbConnection connection, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> =>
        connection.QueryAsync(T.SqlMapper, new SqlStatement(sql), cancellationToken);

    /// <inheritdoc cref="QueryAsync{T}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static Task<List<T>> QueryAsync<T>(
        this DbConnection connection, SqlStatement sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> =>
        connection.QueryAsync(T.SqlMapper, sql, cancellationToken);

    /// <summary>Runs the query and maps the first row, or returns <see langword="default"/> when empty.</summary>
    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this DbConnection connection, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> =>
        connection.QueryFirstOrDefaultAsync(T.SqlMapper, new SqlStatement(sql), cancellationToken);

    /// <inheritdoc cref="QueryFirstOrDefaultAsync{T}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this DbConnection connection, SqlStatement sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> =>
        connection.QueryFirstOrDefaultAsync(T.SqlMapper, sql, cancellationToken);

    // ---- Self-mapping happy path: DbDataSource (opens/disposes a pooled connection) ---------------

    /// <inheritdoc cref="QueryAsync{T}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static Task<List<T>> QueryAsync<T>(
        this DbDataSource dataSource, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> =>
        dataSource.QueryAsync<T>(new SqlStatement(sql), cancellationToken);

    /// <inheritdoc cref="QueryAsync{T}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<List<T>> QueryAsync<T>(
        this DbDataSource dataSource, SqlStatement sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false)) {
            return await connection.QueryAsync<T>(sql, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="QueryFirstOrDefaultAsync{T}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this DbDataSource dataSource, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> =>
        dataSource.QueryFirstOrDefaultAsync<T>(new SqlStatement(sql), cancellationToken);

    /// <inheritdoc cref="QueryFirstOrDefaultAsync{T}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<T?> QueryFirstOrDefaultAsync<T>(
        this DbDataSource dataSource, SqlStatement sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false)) {
            return await connection.QueryFirstOrDefaultAsync<T>(sql, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- Explicit-mapper escape hatch (hand-written mappers, non-[SqlRecord] shapes) --------------

    /// <summary>Runs the query and materializes every row through <paramref name="mapper"/>.</summary>
    public static Task<List<T>> QueryAsync<T>(
        this DbConnection connection, ISqlRowMapper<T> mapper, SqlInterpolatedStringHandler sql,
        CancellationToken cancellationToken = default) =>
        connection.QueryAsync(mapper, new SqlStatement(sql), cancellationToken);

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
        connection.QueryFirstOrDefaultAsync(mapper, new SqlStatement(sql), cancellationToken);

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

    // ---- Non-query / scalar (no mapper): DbConnection ---------------------------------------------

    /// <summary>Executes a non-query statement and returns the affected row count.</summary>
    public static Task<int> ExecuteAsync(
        this DbConnection connection, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default) =>
        connection.ExecuteAsync(new SqlStatement(sql), cancellationToken);

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
        connection.ExecuteScalarAsync<TResult>(new SqlStatement(sql), cancellationToken);

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

    // ---- Non-query / scalar (no mapper): DbDataSource ---------------------------------------------

    /// <inheritdoc cref="ExecuteAsync(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static Task<int> ExecuteAsync(
        this DbDataSource dataSource, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default) =>
        dataSource.ExecuteAsync(new SqlStatement(sql), cancellationToken);

    /// <inheritdoc cref="ExecuteAsync(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<int> ExecuteAsync(
        this DbDataSource dataSource, SqlStatement sql, CancellationToken cancellationToken = default) {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false)) {
            return await connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="ExecuteScalarAsync{TResult}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static Task<TResult?> ExecuteScalarAsync<TResult>(
        this DbDataSource dataSource, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default) =>
        dataSource.ExecuteScalarAsync<TResult>(new SqlStatement(sql), cancellationToken);

    /// <inheritdoc cref="ExecuteScalarAsync{TResult}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<TResult?> ExecuteScalarAsync<TResult>(
        this DbDataSource dataSource, SqlStatement sql, CancellationToken cancellationToken = default) {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false)) {
            return await connection.ExecuteScalarAsync<TResult>(sql, cancellationToken).ConfigureAwait(false);
        }
    }
}
