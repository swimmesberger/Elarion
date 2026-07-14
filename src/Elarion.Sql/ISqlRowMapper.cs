using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// The explicit row-mapping contract a <see cref="SqlRecordAttribute">[SqlRecord]</see> type generates
/// (ADR-0058). A mapper is a value you pass around — helper-method indirection is free, and there is no
/// reflection twin to fall back to. Implementations resolve column ordinals by name once per result set
/// and then read each row with synchronous typed <c>GetFieldValue&lt;T&gt;</c> calls: no per-row name
/// lookups, no boxing, no per-column <c>await</c>.
/// </summary>
/// <typeparam name="T">The row type.</typeparam>
public interface ISqlRowMapper<T> {
    /// <summary>
    /// Reads the current row of <paramref name="reader"/> into a <typeparamref name="T"/>, resolving
    /// column ordinals by name for this call. For multi-row loops prefer <see cref="ReadAll"/> /
    /// <see cref="ReadAllAsync"/> (or the generated <c>Read(reader, in Ordinals)</c> overload), which
    /// resolve ordinals once per result set.
    /// </summary>
    T Read(DbDataReader reader);

    /// <summary>Reads all remaining rows synchronously; ordinals are resolved once.</summary>
    List<T> ReadAll(DbDataReader reader);

    /// <summary>Reads all remaining rows asynchronously; ordinals are resolved once.</summary>
    Task<List<T>> ReadAllAsync(DbDataReader reader, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams remaining rows without buffering; ordinals are resolved once. Backs the unbuffered query
    /// path for large result sets — O(1) per query, O(0) per row.
    /// </summary>
    IAsyncEnumerable<T> ReadAllStreamAsync(DbDataReader reader, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds one typed <see cref="DbParameter"/> per mapped column of <paramref name="row"/> to
    /// <paramref name="command"/>, named exactly like the column (so hand-written SQL binds as
    /// <c>@column_name</c>). <see langword="null"/> values bind as <see cref="DBNull"/>.
    /// </summary>
    void BindParameters(DbCommand command, T row);
}
