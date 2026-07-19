using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Elarion.Devices.EntityFrameworkCore;

/// <summary>
/// Builds the raw statements the stores execute (change-tracker-free, resolved against the mapped
/// model so table/column overrides are honored). Provider-specific by design: the
/// <c>ON CONFLICT</c> insert and <c>DELETE … RETURNING</c> claim shapes require PostgreSQL.
/// </summary>
internal static class DeviceIdentityEntitySql {
    public static string BuildKeyUpsertSql(DbContext context) {
        var (table, column, _) = Resolve(context, typeof(DeviceKeyEntity));
        var keyColumn = column(nameof(DeviceKeyEntity.Key));
        var createdColumn = column(nameof(DeviceKeyEntity.CreatedOnUtc));
        // ON CONFLICT DO UPDATE: redeeming a code issued for an already-provisioned device id is
        // the re-key authorization, so the write rotates the key instead of failing.
        return $"INSERT INTO {table} (" +
               $"{column(nameof(DeviceKeyEntity.DeviceId))}, {keyColumn}, {createdColumn}) " +
               "VALUES ({0}, {1}, {2}) " +
               $"ON CONFLICT ({column(nameof(DeviceKeyEntity.DeviceId))}) DO UPDATE SET " +
               $"{keyColumn} = EXCLUDED.{keyColumn}, {createdColumn} = EXCLUDED.{createdColumn}";
    }

    public static string BuildCodeInsertSql(DbContext context) {
        var (table, column, _) = Resolve(context, typeof(DevicePairingCodeEntity));
        return $"INSERT INTO {table} (" +
               $"{column(nameof(DevicePairingCodeEntity.CodeHash))}, {column(nameof(DevicePairingCodeEntity.DeviceId))}, " +
               $"{column(nameof(DevicePairingCodeEntity.ExpiresOnUtc))}, {column(nameof(DevicePairingCodeEntity.CreatedOnUtc))}) " +
               "VALUES ({0}, {1}, {2}, {3}) " +
               $"ON CONFLICT ({column(nameof(DevicePairingCodeEntity.CodeHash))}) DO NOTHING";
    }

    public static string BuildCodeClaimSql(DbContext context) {
        var (table, column, sqlHelper) = Resolve(context, typeof(DevicePairingCodeEntity));
        // Single-statement claim: delete-if-live and return the claimed row, so exactly one
        // concurrent redeem observes it. Aliases must match ClaimedPairingCodeRow's properties.
        return $"DELETE FROM {table} " +
               $"WHERE {column(nameof(DevicePairingCodeEntity.CodeHash))} = {{0}} " +
               $"AND {column(nameof(DevicePairingCodeEntity.ExpiresOnUtc))} > {{1}} " +
               $"RETURNING {column(nameof(DevicePairingCodeEntity.CodeHash))} AS {sqlHelper.DelimitIdentifier(nameof(ClaimedPairingCodeRow.CodeHash))}, " +
               $"{column(nameof(DevicePairingCodeEntity.DeviceId))} AS {sqlHelper.DelimitIdentifier(nameof(ClaimedPairingCodeRow.DeviceId))}, " +
               $"{column(nameof(DevicePairingCodeEntity.ExpiresOnUtc))} AS {sqlHelper.DelimitIdentifier(nameof(ClaimedPairingCodeRow.ExpiresOnUtc))}";
    }

    public static string BuildCodeSupersedeSql(DbContext context) {
        var (table, column, _) = Resolve(context, typeof(DevicePairingCodeEntity));
        return $"DELETE FROM {table} WHERE {column(nameof(DevicePairingCodeEntity.DeviceId))} = {{0}} " +
               $"AND {column(nameof(DevicePairingCodeEntity.CodeHash))} <> {{1}}";
    }

    private static (string Table, Func<string, string> Column, ISqlGenerationHelper SqlHelper) Resolve(
        DbContext context,
        Type clrType) {
        var entityType = context.Model.FindEntityType(clrType)
                         ?? throw new InvalidOperationException(
                             $"The {clrType.Name} is not mapped. Call modelBuilder.UseElarionDeviceIdentity() in OnModelCreating "
                             + "or annotate the context with [GenerateElarionDeviceIdentity].");
        var sqlHelper = context.GetService<ISqlGenerationHelper>();

        var tableName = entityType.GetTableName()
                        ?? throw new InvalidOperationException($"The {clrType.Name} is not mapped to a table.");
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        string Column(string propertyName) {
            var property = entityType.FindProperty(propertyName)
                           ?? throw new InvalidOperationException(
                               $"The {clrType.Name}.{propertyName} property is not mapped.");
            var columnName = property.GetColumnName(storeObject)
                             ?? throw new InvalidOperationException(
                                 $"The {clrType.Name}.{propertyName} property has no column.");
            return sqlHelper.DelimitIdentifier(columnName);
        }

        return (sqlHelper.DelimitIdentifier(tableName, schema), Column, sqlHelper);
    }
}
