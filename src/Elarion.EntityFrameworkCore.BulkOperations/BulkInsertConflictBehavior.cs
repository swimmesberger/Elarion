namespace Elarion.EntityFrameworkCore.BulkOperations;

/// <summary>What a bulk insert does when a row conflicts with an existing unique constraint.</summary>
public enum BulkInsertConflictBehavior {
    /// <summary>
    /// The default and fastest path: rows stream straight into the target table and any conflict
    /// aborts the whole (all-or-nothing) insert with the database's constraint violation.
    /// </summary>
    Throw = 0,

    /// <summary>
    /// Upsert, skip variant: conflicting rows are left untouched, the rest insert. Stages through a
    /// temporary table, so it costs one extra server-side hop over <see cref="Throw"/>.
    /// </summary>
    DoNothing,

    /// <summary>
    /// Upsert, overwrite variant: conflicting rows are updated to the incoming values (every
    /// insertable non-conflict-target column), the rest insert. Stages through a temporary table.
    /// </summary>
    Update
}
