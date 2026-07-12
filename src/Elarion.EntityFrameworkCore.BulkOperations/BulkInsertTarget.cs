using Microsoft.EntityFrameworkCore.Metadata;

namespace Elarion.EntityFrameworkCore.BulkOperations;

/// <summary>
/// The resolved insert shape for one entity type: the target table and the ordered list of columns a
/// bulk insert writes. Produced by <see cref="BulkInsertTargetResolver"/>; providers turn it into their
/// native bulk mechanism (the column order here is the column order on the wire).
/// </summary>
public sealed class BulkInsertTarget {
    /// <summary>The entity type the target was resolved for.</summary>
    public required IEntityType EntityType { get; init; }

    /// <summary>The table the entity type maps to.</summary>
    public required StoreObjectIdentifier StoreObject { get; init; }

    /// <summary>The insertable columns, in the order they must be written.</summary>
    public required IReadOnlyList<BulkInsertColumn> Columns { get; init; }

    /// <summary>
    /// <see langword="true"/> when the entity type has derived types in the model, in which case every
    /// instance's runtime type must equal the target type — a derived instance carries columns the
    /// resolved column list does not cover, so writing it would silently drop data.
    /// </summary>
    public required bool RequiresExactRuntimeType { get; init; }
}
