using System.Data;
using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// Internal read plumbing over a <see cref="DbConnection"/> — the command/reader ceremony behind the single
/// public convenience surface, <see cref="SqlSessionExtensions"/> on <see cref="ISqlSession"/>. Deliberately
/// thin: no ORM, no statement generation. A closed connection is opened for the call and closed afterwards
/// (Dapper semantics); an already-open connection is left open. Not public: a raw-connection twin of the
/// session surface would look identical at the call site but skip transaction enlistment — callers who own a
/// connection bridge through <see cref="SqlConnectionSessionExtensions.AsSqlSession"/> instead.
/// </summary>
internal static class SqlDbConnectionExtensions {
    // ---- Self-mapping happy path ------------------------------------------------------------------

    /// <summary>Runs the query and materializes every row through the row type's generated mapper.</summary>
    public static Task<List<T>> QueryAsync<T>(
        this DbConnection connection, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        return connection.QueryAsync(T.SqlMapper, new SqlStatement(sql), cancellationToken);
    }

    /// <inheritdoc cref="QueryAsync{T}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static Task<List<T>> QueryAsync<T>(
        this DbConnection connection, SqlStatement sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        return connection.QueryAsync(T.SqlMapper, sql, cancellationToken);
    }

    /// <summary>Runs the query and maps the first row, or returns <see langword="default"/> when empty.</summary>
    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this DbConnection connection, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        return connection.QueryFirstOrDefaultAsync(T.SqlMapper, new SqlStatement(sql), cancellationToken);
    }

    /// <inheritdoc cref="QueryFirstOrDefaultAsync{T}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this DbConnection connection, SqlStatement sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        return connection.QueryFirstOrDefaultAsync(T.SqlMapper, sql, cancellationToken);
    }

    // ---- Explicit-mapper escape hatch (hand-written mappers, non-[SqlRecord] shapes) --------------

    /// <summary>Runs the query and materializes every row through <paramref name="mapper"/>.</summary>
    public static Task<List<T>> QueryAsync<T>(
        this DbConnection connection, ISqlRowMapper<T> mapper, SqlInterpolatedStringHandler sql,
        CancellationToken cancellationToken = default) {
        return connection.QueryAsync(mapper, new SqlStatement(sql), cancellationToken);
    }

    /// <inheritdoc cref="QueryAsync{T}(DbConnection, ISqlRowMapper{T}, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<List<T>> QueryAsync<T>(
        this DbConnection connection, ISqlRowMapper<T> mapper, SqlStatement sql,
        CancellationToken cancellationToken = default) {
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

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
            if (wasClosed) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Runs the query and maps the first row, or returns <see langword="default"/> when empty.</summary>
    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this DbConnection connection, ISqlRowMapper<T> mapper, SqlInterpolatedStringHandler sql,
        CancellationToken cancellationToken = default) {
        return connection.QueryFirstOrDefaultAsync(mapper, new SqlStatement(sql), cancellationToken);
    }

    /// <inheritdoc cref="QueryFirstOrDefaultAsync{T}(DbConnection, ISqlRowMapper{T}, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<T?> QueryFirstOrDefaultAsync<T>(
        this DbConnection connection, ISqlRowMapper<T> mapper, SqlStatement sql,
        CancellationToken cancellationToken = default) {
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

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
            if (wasClosed) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    // ---- Non-query / scalar (no mapper) -----------------------------------------------------------

    /// <summary>Executes a non-query statement and returns the affected row count.</summary>
    public static Task<int> ExecuteAsync(
        this DbConnection connection, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default) {
        return connection.ExecuteAsync(new SqlStatement(sql), cancellationToken);
    }

    /// <inheritdoc cref="ExecuteAsync(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<int> ExecuteAsync(
        this DbConnection connection, SqlStatement sql, CancellationToken cancellationToken = default) {
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try {
            var command = sql.CreateCommand(connection);
            await using (command.ConfigureAwait(false)) {
                return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally {
            if (wasClosed) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes the statement and returns the first column of the first row, or
    /// <see langword="default"/> when the result is empty or <see cref="DBNull"/>.
    /// </summary>
    public static Task<TResult?> ExecuteScalarAsync<TResult>(
        this DbConnection connection, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default) {
        return connection.ExecuteScalarAsync<TResult>(new SqlStatement(sql), cancellationToken);
    }

    /// <inheritdoc cref="ExecuteScalarAsync{TResult}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<TResult?> ExecuteScalarAsync<TResult>(
        this DbConnection connection, SqlStatement sql, CancellationToken cancellationToken = default) {
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try {
            var command = sql.CreateCommand(connection);
            await using (command.ConfigureAwait(false)) {
                var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return value is TResult result ? result : default;
            }
        }
        finally {
            if (wasClosed) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    // ---- Single-row (throws on more than one) -----------------------------------------------------

    /// <summary>
    /// Runs the query and maps the single row, or returns <see langword="default"/> when empty; throws
    /// <see cref="InvalidOperationException"/> if the query returns more than one row (a stricter
    /// get-by-key than <c>QueryFirstOrDefaultAsync</c>, which silently takes the first of many).
    /// </summary>
    public static Task<T?> QuerySingleOrDefaultAsync<T>(
        this DbConnection connection, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        return connection.QuerySingleOrDefaultAsync<T>(new SqlStatement(sql), cancellationToken);
    }

    /// <inheritdoc cref="QuerySingleOrDefaultAsync{T}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async Task<T?> QuerySingleOrDefaultAsync<T>(
        this DbConnection connection, SqlStatement sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        var mapper = T.SqlMapper;
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try {
            var command = sql.CreateCommand(connection);
            await using (command.ConfigureAwait(false)) {
                var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken)
                    .ConfigureAwait(false);
                await using (reader.ConfigureAwait(false)) {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return default;

                    var result = mapper.Read(reader);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException(
                            "The query returned more than one row but a single row was expected.");

                    return result;
                }
            }
        }
        finally {
            if (wasClosed) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    // ---- Streaming (unbuffered) — O(1) per query, O(0) per row ------------------------------------

    /// <summary>
    /// Streams the query's rows without buffering them into a list — for large exports. The connection
    /// is held open for the whole enumeration; enumerate fully (or break) promptly.
    /// </summary>
    public static IAsyncEnumerable<T> QueryUnbufferedAsync<T>(
        this DbConnection connection, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        return connection.QueryUnbufferedAsync<T>(new SqlStatement(sql), cancellationToken);
    }

    /// <inheritdoc cref="QueryUnbufferedAsync{T}(DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
    public static async IAsyncEnumerable<T> QueryUnbufferedAsync<T>(
        this DbConnection connection, SqlStatement sql,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        var mapper = T.SqlMapper;
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try {
            var command = sql.CreateCommand(connection);
            await using (command.ConfigureAwait(false)) {
                var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken)
                    .ConfigureAwait(false);
                await using (reader.ConfigureAwait(false)) {
                    await foreach
                        (var row in mapper.ReadAllStreamAsync(reader, cancellationToken).ConfigureAwait(false))
                        yield return row;
                }
            }
        }
        finally {
            if (wasClosed) await connection.CloseAsync().ConfigureAwait(false);
        }
    }
}
