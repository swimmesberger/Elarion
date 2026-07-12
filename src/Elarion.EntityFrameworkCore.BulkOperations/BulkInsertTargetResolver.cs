using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Elarion.EntityFrameworkCore.BulkOperations;

/// <summary>
/// Resolves the insert shape (<see cref="BulkInsertTarget"/>) for an entity type from EF model
/// metadata, shared by every provider so the column rules stay identical across databases.
/// </summary>
/// <remarks>
/// The rules mirror what EF's own change-tracked INSERT would send, minus everything a non-tracking
/// bulk path cannot honor: store-generated columns are omitted (the database fills them; their values
/// are deliberately not fetched back), client-side value generators do not run (callers assign keys —
/// with Elarion's client-assigned v7 Guid convention, ADR-0038, entities own their ids anyway), and
/// unsupported mapping shapes fail loud rather than writing partial rows.
/// </remarks>
public static class BulkInsertTargetResolver {
    /// <summary>Resolves the bulk-insert target for <paramref name="entityClrType"/> in <paramref name="model"/>.</summary>
    /// <param name="model">The EF model.</param>
    /// <param name="entityClrType">The CLR entity type being inserted.</param>
    /// <param name="isProviderStoreGenerated">
    /// Optional provider refinement recognizing store-generated columns the relational metadata alone
    /// cannot (e.g. Npgsql identity/serial strategies). Such columns are omitted from the insert.
    /// </param>
    /// <exception cref="InvalidOperationException">The type is not part of the model or not mapped to a table.</exception>
    /// <exception cref="NotSupportedException">The mapping uses a shape bulk insert does not support.</exception>
    public static BulkInsertTarget Resolve(
        IModel model,
        Type entityClrType,
        Func<IProperty, StoreObjectIdentifier, bool>? isProviderStoreGenerated = null) {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(entityClrType);

        var entityType = model.FindEntityType(entityClrType)
            ?? throw new InvalidOperationException(
                $"The type '{entityClrType.Name}' is not part of the EF Core model for this context.");

        if (entityType.ClrType.IsAbstract) {
            throw new InvalidOperationException(
                $"Cannot bulk insert the abstract type '{entityClrType.Name}'. Insert via a DbSet of a concrete derived type.");
        }

        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException(
                $"The entity type '{entityType.DisplayName()}' is not mapped to a table; bulk insert requires a table mapping.");
        var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());

        if (entityType.GetTableMappings().Take(2).Count() > 1) {
            throw new NotSupportedException(
                $"The entity type '{entityType.DisplayName()}' maps to more than one table (TPT or entity splitting), which bulk insert does not support.");
        }

        var ownedNavigation = entityType.GetNavigations().FirstOrDefault(n => n.ForeignKey.IsOwnership && !n.IsOnDependent);
        if (ownedNavigation is not null) {
            throw new NotSupportedException(
                $"The entity type '{entityType.DisplayName()}' owns '{ownedNavigation.Name}'; owned types are not supported by bulk insert.");
        }

        var columns = new List<BulkInsertColumn>();
        CollectColumns(entityType, entityType, [], storeObject, isProviderStoreGenerated, columns);

        return new BulkInsertTarget {
            EntityType = entityType,
            StoreObject = storeObject,
            Columns = columns,
            RequiresExactRuntimeType = entityType.GetDirectlyDerivedTypes().Any(),
        };
    }

    // Walks one structural type (the entity, or a complex type reached through `complexPath`) and
    // appends its insertable columns in declaration order; complex properties flatten recursively —
    // they always share the entity's table, so every leaf is a plain column of the same store object.
    private static void CollectColumns(
        IEntityType entityType,
        ITypeBase structuralType,
        IReadOnlyList<IComplexProperty> complexPath,
        in StoreObjectIdentifier storeObject,
        Func<IProperty, StoreObjectIdentifier, bool>? isProviderStoreGenerated,
        List<BulkInsertColumn> columns) {
        var discriminatorProperty = entityType.FindDiscriminatorProperty();
        foreach (var property in structuralType.GetProperties()) {
            var columnName = property.GetColumnName(storeObject);
            if (columnName is null) {
                continue;
            }

            if (property == discriminatorProperty) {
                columns.Add(new BulkInsertColumn {
                    ColumnName = columnName,
                    Property = property,
                    IsDiscriminator = true,
                    DiscriminatorValue = entityType.GetDiscriminatorValue(),
                });
                continue;
            }

            if (IsStoreGenerated(property, storeObject) || (isProviderStoreGenerated?.Invoke(property, storeObject) ?? false)) {
                continue;
            }

            if (property.GetBeforeSaveBehavior() == PropertySaveBehavior.Ignore) {
                continue;
            }

            if (property.IsShadowProperty()) {
                throw new NotSupportedException(
                    $"The entity type '{entityType.DisplayName()}' has the insertable shadow property '{property.Name}'; " +
                    "shadow values only exist on tracked entries, which a non-tracking bulk insert does not have. " +
                    "Map the property to a CLR member or exclude the entity type from bulk insert.");
            }

            columns.Add(new BulkInsertColumn { ColumnName = columnName, Property = property, ComplexPath = complexPath });
        }

        foreach (var complexProperty in structuralType.GetComplexProperties()) {
            if (complexProperty.IsCollection) {
                throw new NotSupportedException(
                    $"The entity type '{entityType.DisplayName()}' has the complex collection '{complexProperty.Name}' " +
                    "(JSON-mapped), which bulk insert does not support.");
            }

            IReadOnlyList<IComplexProperty> childPath = [.. complexPath, complexProperty];
            CollectColumns(entityType, complexProperty.ComplexType, childPath, storeObject, isProviderStoreGenerated, columns);
        }
    }

    // Store-generated per relational metadata alone: computed columns always; on-update generation
    // (concurrency tokens like xmin / rowversion) always; on-add generation only when the store
    // demonstrably fills the column (a default constraint) — an on-add property WITHOUT a store
    // default is client-generated, and since value generators need tracked entries the caller must
    // have assigned it, so it stays in the column list.
    private static bool IsStoreGenerated(IProperty property, in StoreObjectIdentifier storeObject) {
        if (property.GetComputedColumnSql(storeObject) is not null) {
            return true;
        }

        return property.ValueGenerated switch {
            ValueGenerated.OnAddOrUpdate or ValueGenerated.OnUpdate or ValueGenerated.OnUpdateSometimes => true,
            ValueGenerated.OnAdd => property.GetDefaultValueSql(storeObject) is not null
                || property.TryGetDefaultValue(storeObject, out _),
            _ => false,
        };
    }
}
