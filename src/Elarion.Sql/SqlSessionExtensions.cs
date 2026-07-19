using System.Runtime.CompilerServices;

namespace Elarion.Sql;

/// <summary>
/// Query/execute convenience over an <see cref="ISqlSession"/>: the same thin surface as the
/// <see cref="SqlDbConnectionExtensions">DbConnection</see>/<see cref="SqlWriteExtensions">write</see>
/// helpers, but run against the session's shared connection with <see cref="ISqlSession.CurrentTransaction"/>
/// enlisted automatically. A handler injects <see cref="ISqlSession"/> and calls these, so its reads and writes
/// join the unit of work the framework opened without ever touching a <c>DbTransaction</c> by hand.
/// </summary>
/// <remarks>
/// Reads run on the session's connection, which — when a unit of work is active — is mid-transaction; on the
/// supported PostgreSQL/Npgsql provider every statement on that connection participates in the open transaction,
/// so reads observe the scope's own uncommitted writes. Writes pass <see cref="ISqlSession.CurrentTransaction"/>
/// through explicitly (it may be <see langword="null"/> when no unit of work is active, in which case each call
/// runs autonomously, matching the raw <c>DbConnection</c> helpers).
/// </remarks>
public static class SqlSessionExtensions {
    // ---- Reads (self-mapping happy path) ----------------------------------------------------------

    /// <inheritdoc cref="SqlDbConnectionExtensions.QueryAsync{T}(System.Data.Common.DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
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

    /// <inheritdoc cref="SqlDbConnectionExtensions.QueryFirstOrDefaultAsync{T}(System.Data.Common.DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
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

    /// <inheritdoc cref="SqlDbConnectionExtensions.QuerySingleOrDefaultAsync{T}(System.Data.Common.DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
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

    /// <inheritdoc cref="SqlDbConnectionExtensions.QueryUnbufferedAsync{T}(System.Data.Common.DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
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

    // ---- Writes (enlist the ambient transaction) --------------------------------------------------

    /// <inheritdoc cref="SqlWriteExtensions.InsertAsync{T}(System.Data.Common.DbConnection, T, System.Data.Common.DbTransaction, CancellationToken)"/>
    public static async Task<int> InsertAsync<T>(
        this ISqlSession session, T row, CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.InsertAsync(row, session.CurrentTransaction, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="SqlWriteExtensions.InsertManyAsync{T}(System.Data.Common.DbConnection, IEnumerable{T}, string, System.Data.Common.DbTransaction, CancellationToken)"/>
    public static async Task<int> InsertManyAsync<T>(
        this ISqlSession session, IEnumerable<T> rows, string? sqlSuffix = null,
        CancellationToken cancellationToken = default)
        where T : ISqlRecord<T> {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.InsertManyAsync(rows, sqlSuffix, session.CurrentTransaction, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc cref="SqlWriteExtensions.ExecuteAsync(System.Data.Common.DbConnection, SqlStatement, System.Data.Common.DbTransaction, CancellationToken)"/>
    public static async Task<int> ExecuteAsync(
        this ISqlSession session, SqlStatement sql, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        // With a unit of work active the statement enlists the transaction; without one it runs autonomously,
        // matching the raw DbConnection helper.
        return session.CurrentTransaction is { } transaction
            ? await connection.ExecuteAsync(sql, transaction, cancellationToken).ConfigureAwait(false)
            : await connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ExecuteAsync(ISqlSession, SqlStatement, CancellationToken)"/>
    public static Task<int> ExecuteAsync(
        this ISqlSession session, SqlInterpolatedStringHandler sql, CancellationToken cancellationToken = default) {
        return session.ExecuteAsync(new SqlStatement(sql), cancellationToken);
    }

    /// <inheritdoc cref="SqlDbConnectionExtensions.ExecuteScalarAsync{TResult}(System.Data.Common.DbConnection, SqlInterpolatedStringHandler, CancellationToken)"/>
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
