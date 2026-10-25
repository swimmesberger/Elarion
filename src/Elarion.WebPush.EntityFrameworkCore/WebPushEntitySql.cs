using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Elarion.WebPush.EntityFrameworkCore;

/// <summary>
/// Builds the raw statements the stores execute (change-tracker-free, resolved against the mapped model so
/// table/column overrides are honored) and their parameters. The <c>INSERT … ON CONFLICT</c> shapes run on
/// PostgreSQL and SQLite (3.24+); another provider replaces the stores through the
/// <see cref="IPushSubscriptionStore"/> / <see cref="IVapidKeyStore"/> seams.
/// </summary>
internal static class WebPushEntitySql {
    public static string BuildSubscriptionUpsertSql(DbContext context) {
        var (table, column) = Resolve(context, typeof(PushSubscriptionEntity));
        var endpoint = column(nameof(PushSubscriptionEntity.Endpoint));
        var p256dh = column(nameof(PushSubscriptionEntity.P256dh));
        var auth = column(nameof(PushSubscriptionEntity.Auth));
        var userId = column(nameof(PushSubscriptionEntity.UserId));
        var userAgent = column(nameof(PushSubscriptionEntity.UserAgent));
        var created = column(nameof(PushSubscriptionEntity.CreatedOnUtc));
        var lastSeen = column(nameof(PushSubscriptionEntity.LastSeenOnUtc));
        // One statement, so a device re-subscribing under another account (same endpoint) reassigns the row
        // atomically instead of colliding on the unique endpoint index. The id and — for the same owner — the
        // creation time survive; a new owner restarts the creation time.
        // The WHERE guards the takeover inside that statement, so no check-then-act race exists: only the owner,
        // or a caller presenting the stored auth secret (the same browser under another account), updates the
        // row; any other conflict updates nothing and the statement reports zero rows. Under READ COMMITTED a
        // concurrent insert of the same endpoint waits on the unique index and evaluates the WHERE against the
        // committed winner. The auth comparison is plain equality, not constant-time; IPushSubscriptionStore.UpsertAsync
        // documents why that is acceptable.
        return $"INSERT INTO {table} AS existing ({column(nameof(PushSubscriptionEntity.Id))}, {endpoint}, {p256dh}, {auth}, " +
               $"{userId}, {userAgent}, {created}, {lastSeen}) " +
               "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}) " +
               $"ON CONFLICT ({endpoint}) DO UPDATE SET " +
               $"{p256dh} = EXCLUDED.{p256dh}, {auth} = EXCLUDED.{auth}, " +
               $"{created} = CASE WHEN existing.{userId} = EXCLUDED.{userId} THEN existing.{created} ELSE EXCLUDED.{created} END, " +
               $"{userId} = EXCLUDED.{userId}, {userAgent} = EXCLUDED.{userAgent}, {lastSeen} = EXCLUDED.{lastSeen} " +
               $"WHERE existing.{userId} = EXCLUDED.{userId} OR existing.{auth} = EXCLUDED.{auth}";
    }

    public static string BuildVapidKeyInsertSql(DbContext context) {
        var (table, column) = Resolve(context, typeof(VapidKeyEntity));
        // DO NOTHING: when another node won the first insert, the loser reads the winner back.
        return $"INSERT INTO {table} ({column(nameof(VapidKeyEntity.Name))}, {column(nameof(VapidKeyEntity.PublicKey))}, " +
               $"{column(nameof(VapidKeyEntity.PrivateKey))}, {column(nameof(VapidKeyEntity.Protection))}, " +
               $"{column(nameof(VapidKeyEntity.CreatedOnUtc))}) " +
               "VALUES ({0}, {1}, {2}, {3}, {4}) " +
               $"ON CONFLICT ({column(nameof(VapidKeyEntity.Name))}) DO NOTHING";
    }

    /// <summary>
    /// The statement's parameters, in order, each created by the type mapping of the property it is written to.
    /// A raw statement bypasses the change tracker but must still write what a tracked insert would: a value
    /// converter on the model applies (an application that stores <see cref="DateTimeOffset"/> as a sortable
    /// integer, or a <see cref="Guid"/> as text, gets exactly that), and a null is a typed null.
    /// </summary>
    public static DbParameter[] Parameters(DbContext context, Type clrType, params (string Property, object? Value)[] values) {
        var entityType = FindEntityType(context, clrType);
        // The command only serves as the provider's parameter factory; the parameters travel on EF's own command.
        using var factory = context.Database.GetDbConnection().CreateCommand();
        var parameters = new DbParameter[values.Length];
        for (var i = 0; i < values.Length; i++) {
            var (propertyName, value) = values[i];
            var property = entityType.FindProperty(propertyName)
                           ?? throw new InvalidOperationException($"The {clrType.Name}.{propertyName} property is not mapped.");
            parameters[i] = property.GetRelationalTypeMapping()
                .CreateParameter(factory, $"p{i}", value, property.IsNullable);
        }

        return parameters;
    }

    private static IEntityType FindEntityType(DbContext context, Type clrType) {
        return context.Model.FindEntityType(clrType)
               ?? throw new InvalidOperationException(
                   $"The {clrType.Name} is not mapped. Call modelBuilder.UseElarionWebPush() in OnModelCreating "
                   + "or annotate the context with [GenerateElarionWebPush].");
    }

    private static (string Table, Func<string, string> Column) Resolve(DbContext context, Type clrType) {
        var entityType = FindEntityType(context, clrType);
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

        return (sqlHelper.DelimitIdentifier(tableName, schema), Column);
    }
}
