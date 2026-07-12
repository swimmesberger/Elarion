using Microsoft.EntityFrameworkCore.Metadata;

namespace Elarion.EntityFrameworkCore.BulkOperations;

/// <summary>One insertable column of a <see cref="BulkInsertTarget"/>.</summary>
public sealed class BulkInsertColumn {
    /// <summary>The store column name (undelimited).</summary>
    public required string ColumnName { get; init; }

    /// <summary>The EF property the column maps; source of the type mapping and value converter.</summary>
    public required IProperty Property { get; init; }

    /// <summary>
    /// The complex-property chain from the entity to the member holding <see cref="Property"/>; empty
    /// for a direct entity property. A <see langword="null"/> anywhere along the chain nulls the column.
    /// </summary>
    public IReadOnlyList<IComplexProperty> ComplexPath { get; init; } = [];

    /// <summary>
    /// <see langword="true"/> when this is the TPH discriminator column, whose value is the constant
    /// <see cref="DiscriminatorValue"/> rather than a member read off the entity.
    /// </summary>
    public bool IsDiscriminator { get; init; }

    /// <summary>The model-side discriminator constant when <see cref="IsDiscriminator"/> is set.</summary>
    public object? DiscriminatorValue { get; init; }
}
