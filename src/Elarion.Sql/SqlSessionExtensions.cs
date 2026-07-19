using System.Runtime.CompilerServices;

namespace Elarion.Sql;

/// <summary>
/// The SQL tier's <b>single</b> query/execute convenience surface, on <see cref="ISqlSession"/> — a connection
/// paired with its transaction intent. Every call runs on the session's connection and every write enlists
/// <see cref="ISqlSession.CurrentTransaction"/> automatically, so nothing on this surface can accidentally run
/// outside the transaction its scope declared; there is deliberately no per-call transaction parameter and no
/// raw-connection twin of these methods.
/// </summary>
/// <remarks>
/// <para>
/// How you obtain the session states the transaction semantics: a handler injects the <b>scoped</b>
/// <see cref="ISqlSession"/> (writes join the framework unit of work); code that owns its connection — DI-free
/// hosts, tooling, tests, singleton-eligible handlers holding a <see cref="System.Data.Common.DbDataSource"/> —
/// bridges with <see cref="SqlConnectionSessionExtensions.AsSqlSession"/>, choosing its transaction (or per-call
/// auto-commit) once at wrap time.
/// </para>
/// <para>
/// Reads run on the session's connection, which — when a transaction is active — is mid-transaction; on the
/// supported providers (Npgsql, Microsoft.Data.Sqlite) every statement on that connection participates in the
/// open transaction, so reads observe the scope's own uncommitted writes.
/// </para>
/// </remarks>
public static class SqlSessionExtensions {
    // ---- Reads (self-mapping happy path) ----------------------------------------------------------

    /// <summary>Runs the query and materializes every row through the row type's generated mapper.</summary>
    public static async Task<List<T>> QueryAsync<T>(
        this ISqlSession session, SqlStatement sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryAsync<T>(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="QueryAsync{T}(ISqlSession, SqlStatement, CancellationToken)"/>
    public static Task<List<T>> QueryAsync<T>(
        this ISqlSession session, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        return session.QueryAsync<T>(new SqlStatement(sql), cancellationToken);
    }

    /// <summary>Runs the query and maps the first row, or returns <see langword="default"/> when empty.</summary>
    public static async Task<T?> QueryFirstOrDefaultAsync<T>(
        this ISqlSession session, SqlStatement sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<T>(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="QueryFirstOrDefaultAsync{T}(ISqlSession, SqlStatement, CancellationToken)"/>
    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this ISqlSession session, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        return session.QueryFirstOrDefaultAsync<T>(new SqlStatement(sql), cancellationToken);
    }

    /// <summary>
    /// Runs the query and maps the single row, or returns <see langword="default"/> when empty; throws
    /// <see cref="InvalidOperationException"/> if the query returns more than one row (a stricter
    /// get-by-key than <c>QueryFirstOrDefaultAsync</c>, which silently takes the first of many).
    /// </summary>
    public static async Task<T?> QuerySingleOrDefaultAsync<T>(
        this ISqlSession session, SqlStatement sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<T>(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="QuerySingleOrDefaultAsync{T}(ISqlSession, SqlStatement, CancellationToken)"/>
    public static Task<T?> QuerySingleOrDefaultAsync<T>(
        this ISqlSession session, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        return session.QuerySingleOrDefaultAsync<T>(new SqlStatement(sql), cancellationToken);
    }

    /// <summary>
    /// Streams the query's rows without buffering them into a list — for large exports. The session's
    /// connection is held by the enumeration; enumerate fully (or break) promptly.
    /// </summary>
    public static async IAsyncEnumerable<T> QueryUnbufferedAsync<T>(
        this ISqlSession session, SqlStatement sql,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var row in connection.QueryUnbufferedAsync<T>(sql, cancellationToken).ConfigureAwait(false))
            yield return row;
    }

    /// <inheritdoc cref="QueryUnbufferedAsync{T}(ISqlSession, SqlStatement, CancellationToken)"/>
    public static IAsyncEnumerable<T> QueryUnbufferedAsync<T>(
        this ISqlSession session, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        return session.QueryUnbufferedAsync<T>(new SqlStatement(sql), cancellationToken);
    }

    // ---- Explicit-mapper escape hatch (hand-written mappers, non-[SqlRecord] shapes) --------------

    /// <summary>Runs the query and materializes every row through <paramref name="mapper"/>.</summary>
    public static async Task<List<T>> QueryAsync<T>(
        this ISqlSession session, ISqlRowMapper<T> mapper, SqlStatement sql,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryAsync(mapper, sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="QueryAsync{T}(ISqlSession, ISqlRowMapper{T}, SqlStatement, CancellationToken)"/>
    public static Task<List<T>> QueryAsync<T>(
        this ISqlSession session, ISqlRowMapper<T> mapper, SqlInterpolatedStringHandler sql,
        CancellationToken cancellationToken = default) {
        return session.QueryAsync(mapper, new SqlStatement(sql), cancellationToken);
    }

    /// <summary>Runs the query and maps the first row through <paramref name="mapper"/>, or returns
    /// <see langword="default"/> when empty.</summary>
    public static async Task<T?> QueryFirstOrDefaultAsync<T>(
        this ISqlSession session, ISqlRowMapper<T> mapper, SqlStatement sql,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync(mapper, sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="QueryFirstOrDefaultAsync{T}(ISqlSession, ISqlRowMapper{T}, SqlStatement, CancellationToken)"/>
    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this ISqlSession session, ISqlRowMapper<T> mapper, SqlInterpolatedStringHandler sql,
        CancellationToken cancellationToken = default) {
        return session.QueryFirstOrDefaultAsync(mapper, new SqlStatement(sql), cancellationToken);
    }

    // ---- Writes (enlist the session's transaction) ------------------------------------------------

    /// <summary>
    /// Inserts one row via the generated full-row INSERT, enlisting the session's transaction when one is
    /// active; returns the affected row count.
    /// </summary>
    public static async Task<int> InsertAsync<T>(
        this ISqlSession session, T row, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.InsertAsync(row, session.CurrentTransaction, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts every row via the generated full-row INSERT, reusing one prepared command. With a session
    /// transaction active the batch enlists it; without one the batch runs in its own transaction (a batch is
    /// atomic on its own). <paramref name="sqlSuffix"/> is appended verbatim to the INSERT
    /// (e.g. <c>" ON CONFLICT DO NOTHING"</c>). Returns the total affected row count. A convenience batch,
    /// not bulk COPY — for high-throughput bulk load use the binary-COPY path
    /// (<c>Elarion.BulkOperations.PostgreSql</c>, ADR-0051).
    /// </summary>
    public static async Task<int> InsertManyAsync<T>(
        this ISqlSession session, IEnumerable<T> rows, string? sqlSuffix = null,
        CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.InsertManyAsync(rows, sqlSuffix, session.CurrentTransaction, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a non-query statement, enlisting the session's transaction when one is active; returns the
    /// affected row count.
    /// </summary>
    public static async Task<int> ExecuteAsync(
        this ISqlSession session, SqlStatement sql, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        // With a transaction active the statement enlists it; without one it runs autonomously.
        return session.CurrentTransaction is { } transaction
            ? await connection.ExecuteAsync(sql, transaction, cancellationToken).ConfigureAwait(false)
            : await connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ExecuteAsync(ISqlSession, SqlStatement, CancellationToken)"/>
    public static Task<int> ExecuteAsync(
        this ISqlSession session, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default) {
        return session.ExecuteAsync(new SqlStatement(sql), cancellationToken);
    }

    /// <summary>
    /// Executes the statement and returns the first column of the first row, or
    /// <see langword="default"/> when the result is empty or <see cref="DBNull"/>.
    /// </summary>
    public static async Task<TResult?> ExecuteScalarAsync<TResult>(
        this ISqlSession session, SqlStatement sql, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<TResult>(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ExecuteScalarAsync{TResult}(ISqlSession, SqlStatement, CancellationToken)"/>
    public static Task<TResult?> ExecuteScalarAsync<TResult>(
        this ISqlSession session, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default) {
        return session.ExecuteScalarAsync<TResult>(new SqlStatement(sql), cancellationToken);
    }
}
