namespace Elarion.EntityFrameworkCore.BulkOperations;

/// <summary>
/// Options for a single <c>ExecuteInsertAsync</c> call.
/// </summary>
/// <remarks>
/// Deliberately minimal: the bulk mechanism (PostgreSQL binary <c>COPY</c>) streams rows and needs no
/// batch-size or ordering knobs. New behavior arrives as additional optional members rather than new
/// method overloads. The defaults describe the fastest path — anything that costs extra (like the
/// staged upsert behind <see cref="OnConflict"/>) is opt-in per call.
/// </remarks>
public sealed class BulkInsertOptions {
    /// <summary>The shared default instance used when a caller passes no options.</summary>
    public static readonly BulkInsertOptions Default = new();

    /// <summary>
    /// Time limit for the whole bulk operation. <see langword="null"/> uses the provider's default
    /// (for PostgreSQL, Npgsql's command timeout).
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Conflict handling. The default (<see cref="BulkInsertConflictBehavior.Throw"/>) streams
    /// directly into the target table; the upsert behaviors stage through a temporary table and
    /// merge with the database's native conflict clause.
    /// </summary>
    public BulkInsertConflictBehavior OnConflict { get; init; }

    /// <summary>
    /// The entity properties forming the conflict target (a declared primary/alternate key or unique
    /// index). <see langword="null"/> targets the primary key for
    /// <see cref="BulkInsertConflictBehavior.Update"/> and any unique constraint for
    /// <see cref="BulkInsertConflictBehavior.DoNothing"/>. Ignored for
    /// <see cref="BulkInsertConflictBehavior.Throw"/>.
    /// </summary>
    public IReadOnlyList<string>? ConflictProperties { get; init; }
}
