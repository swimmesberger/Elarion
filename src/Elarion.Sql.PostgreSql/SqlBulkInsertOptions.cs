namespace Elarion.Sql;

/// <summary>
/// Conflict handling for <see cref="SqlSessionCopyExtensions.ExecuteInsertAsync{T}(ISqlSession, IEnumerable{T}, SqlBulkInsertOptions?, CancellationToken)"/>
/// — the same vocabulary as the EF tier's <c>BulkInsertConflictBehavior</c> (ADR-0051), so a workload
/// moves between tiers as a receiver change, not a redesign.
/// </summary>
public enum SqlBulkInsertConflictBehavior {
    /// <summary>
    /// Stream straight into the target table (fastest). COPY is all-or-nothing: any conflict or error
    /// aborts the whole stream and no partial rows remain.
    /// </summary>
    Throw = 0,

    /// <summary>Stage through a temporary table and skip rows that violate any unique constraint.</summary>
    DoNothing = 1,

    /// <summary>
    /// Stage through a temporary table and overwrite existing rows: every mapped column outside the
    /// conflict target is set to the incoming value. Requires <see cref="SqlBulkInsertOptions.ConflictColumns"/>.
    /// </summary>
    Update = 2
}

/// <summary>Options bag for the SQL-tier bulk insert (ADR-0068).</summary>
public sealed record SqlBulkInsertOptions {
    /// <summary>Conflict handling; default <see cref="SqlBulkInsertConflictBehavior.Throw"/> streams directly.</summary>
    public SqlBulkInsertConflictBehavior OnConflict { get; init; } = SqlBulkInsertConflictBehavior.Throw;

    /// <summary>
    /// The <c>ON CONFLICT</c> target as mapped column names. Required for
    /// <see cref="SqlBulkInsertConflictBehavior.Update"/> (this tier has no key metadata to infer from);
    /// optional for <see cref="SqlBulkInsertConflictBehavior.DoNothing"/>, which then skips on any unique
    /// constraint. PostgreSQL requires a unique constraint or index over the named columns.
    /// </summary>
    public IReadOnlyList<string>? ConflictColumns { get; init; }

    /// <summary>Timeout applied to the COPY stream and the staging/merge statements.</summary>
    public TimeSpan? Timeout { get; init; }
}
