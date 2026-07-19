using System.Data;
using Elarion.EntityFrameworkCore.BulkOperations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Elarion.BulkOperations.PostgreSql;

/// <summary>
/// <see cref="IBulkInsertProvider"/> over PostgreSQL binary <c>COPY</c>
/// (<see cref="NpgsqlConnection.BeginBinaryImportAsync"/>).
/// </summary>
/// <remarks>
/// Runs on the context's own connection: when the caller holds an open
/// <c>Database.CurrentTransaction</c>, all statements execute inside it and roll back with it. The
/// default (<see cref="BulkInsertConflictBehavior.Throw"/>) streams straight into the target table —
/// COPY is all-or-nothing, any error aborts the whole stream, no partial rows. The upsert behaviors
/// stage the COPY into a per-call temporary table and merge with
/// <c>INSERT … SELECT … ON CONFLICT</c>, because COPY itself cannot express conflict handling. The
/// connection is opened through EF's bookkeeping (<c>Database.OpenConnectionAsync</c>) and closed
/// again only if this call opened it.
/// </remarks>
internal sealed class PostgreSqlBulkInsertProvider : IBulkInsertProvider {
    public async Task<long> ExecuteInsertAsync<TEntity>(
        DbContext context,
        IEnumerable<TEntity> entities,
        BulkInsertOptions options,
        CancellationToken cancellationToken)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(options);

        if (entities is ICollection<TEntity> { Count: 0 } or IReadOnlyCollection<TEntity> { Count: 0 }) return 0;

        var plan = PostgreSqlBulkInsertPlanCache.Get<TEntity>(context);
        Func<NpgsqlBinaryImporter, Task> writeRows = importer =>
            WriteRowsAsync(importer, plan, entities, cancellationToken);
        return options.OnConflict == BulkInsertConflictBehavior.Throw
            ? await DirectInsertAsync(context, plan, options, writeRows, cancellationToken).ConfigureAwait(false)
            : await StagedInsertAsync(context, plan, options, writeRows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> ExecuteInsertAsync<TEntity>(
        DbContext context,
        IAsyncEnumerable<TEntity> entities,
        BulkInsertOptions options,
        CancellationToken cancellationToken)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(options);

        var plan = PostgreSqlBulkInsertPlanCache.Get<TEntity>(context);
        Func<NpgsqlBinaryImporter, Task> writeRows = importer =>
            WriteRowsAsync(importer, plan, entities, cancellationToken);
        return options.OnConflict == BulkInsertConflictBehavior.Throw
            ? await DirectInsertAsync(context, plan, options, writeRows, cancellationToken).ConfigureAwait(false)
            : await StagedInsertAsync(context, plan, options, writeRows, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> DirectInsertAsync<TEntity>(
        DbContext context,
        PostgreSqlBulkInsertPlan<TEntity> plan,
        BulkInsertOptions options,
        Func<NpgsqlBinaryImporter, Task> writeRows,
        CancellationToken cancellationToken)
        where TEntity : class {
        var openedByUs = await OpenConnectionAsync(context, cancellationToken).ConfigureAwait(false);
        try {
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            await using var importer = await connection.BeginBinaryImportAsync(plan.CopyCommand, cancellationToken)
                .ConfigureAwait(false);
            ApplyOptions(importer, options);
            await writeRows(importer).ConfigureAwait(false);
            return (long)await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally {
            if (openedByUs) await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<long> StagedInsertAsync<TEntity>(
        DbContext context,
        PostgreSqlBulkInsertPlan<TEntity> plan,
        BulkInsertOptions options,
        Func<NpgsqlBinaryImporter, Task> writeRows,
        CancellationToken cancellationToken)
        where TEntity : class {
        var sqlHelper = context.GetService<ISqlGenerationHelper>();
        // Random (not time-ordered) suffix: concurrent calls on other connections may stage at the
        // same instant, and temp names only need to be unique within this call's session anyway.
        var tempTable = sqlHelper.DelimitIdentifier("elarion_bulk_stage_" + Guid.NewGuid().ToString("N")[..8]);
        // Conflict metadata is validated before the connection opens, so misconfiguration fails loud
        // without touching the database.
        var mergeSql = BuildMergeSql(plan, options, sqlHelper, tempTable);

        var openedByUs = await OpenConnectionAsync(context, cancellationToken).ConfigureAwait(false);
        try {
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            var transaction = context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;

            var columnDefinitions = plan.DelimitedColumnNames
                .Zip(plan.ColumnStoreTypes, (name, storeType) => $"{name} {storeType}");
            await ExecuteAsync(
                connection, transaction, options,
                $"CREATE TEMP TABLE {tempTable} ({string.Join(", ", columnDefinitions)})",
                cancellationToken).ConfigureAwait(false);
            try {
                await using (var importer = await connection
                                 .BeginBinaryImportAsync(
                                     $"COPY {tempTable} ({plan.DelimitedColumnList}) FROM STDIN (FORMAT BINARY)",
                                     cancellationToken)
                                 .ConfigureAwait(false)) {
                    ApplyOptions(importer, options);
                    await writeRows(importer).ConfigureAwait(false);
                    await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
                }

                return await ExecuteAsync(connection, transaction, options, mergeSql, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally {
                // Best-effort: inside an aborted ambient transaction (or after cancellation) the DROP
                // cannot run, but the temp table dies with the rollback / session in that case anyway.
                try {
                    await ExecuteAsync(connection, transaction, options, $"DROP TABLE IF EXISTS {tempTable}",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception) {
                    // Intentionally swallowed — cleanup only; the original failure is the signal.
                }
            }
        }
        finally {
            if (openedByUs) await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static string BuildMergeSql<TEntity>(
        PostgreSqlBulkInsertPlan<TEntity> plan,
        BulkInsertOptions options,
        ISqlGenerationHelper sqlHelper,
        string delimitedTempTable)
        where TEntity : class {
        var entityType = plan.Target.EntityType;
        var storeObject = plan.Target.StoreObject;

        IReadOnlyList<IProperty>? conflictProperties = null;
        if (options.ConflictProperties is { Count: > 0 } names)
            conflictProperties = [
                .. names.Select(name => entityType.FindProperty(name)
                                        ?? throw new InvalidOperationException(
                                            $"Conflict property '{name}' is not a property of '{entityType.DisplayName()}'."))
            ];
        else if (options.OnConflict == BulkInsertConflictBehavior.Update)
            conflictProperties = entityType.FindPrimaryKey()?.Properties
                                 ?? throw new InvalidOperationException(
                                     $"'{entityType.DisplayName()}' has no primary key; set BulkInsertOptions.ConflictProperties to name the update conflict target.");

        var conflictTarget = "";
        if (conflictProperties is not null) {
            ValidateUniqueConstraint(entityType, conflictProperties);
            conflictTarget = " (" + string.Join(", ", conflictProperties.Select(p =>
                                 sqlHelper.DelimitIdentifier(p.GetColumnName(storeObject)
                                                             ?? throw new InvalidOperationException(
                                                                 $"Conflict property '{p.Name}' is not mapped to a column of '{plan.Target.StoreObject.DisplayName()}'.")))) +
                             ")";
        }

        var insert = $"INSERT INTO {plan.DelimitedTable} ({plan.DelimitedColumnList}) " +
                     $"SELECT {plan.DelimitedColumnList} FROM {delimitedTempTable}";
        if (options.OnConflict == BulkInsertConflictBehavior.DoNothing)
            return $"{insert} ON CONFLICT{conflictTarget} DO NOTHING";

        var conflictColumns = conflictProperties!.Select(p => p.GetColumnName(storeObject))
            .ToHashSet(StringComparer.Ordinal);
        var assignments = plan.Target.Columns
            .Where(c => !c.IsDiscriminator && !conflictColumns.Contains(c.ColumnName))
            .Select(c => sqlHelper.DelimitIdentifier(c.ColumnName))
            .Select(c => $"{c} = EXCLUDED.{c}")
            .ToList();
        if (assignments.Count == 0)
            throw new InvalidOperationException(
                $"Every insertable column of '{entityType.DisplayName()}' is part of the conflict target; " +
                "there is nothing to update — use BulkInsertConflictBehavior.DoNothing.");

        return $"{insert} ON CONFLICT{conflictTarget} DO UPDATE SET {string.Join(", ", assignments)}";
    }

    private static void ValidateUniqueConstraint(IEntityType entityType, IReadOnlyList<IProperty> properties) {
        var set = properties.ToHashSet();
        var matches = entityType.GetKeys().Any(k => k.Properties.Count == set.Count && k.Properties.All(set.Contains))
                      || entityType.GetIndexes().Any(i =>
                          i.IsUnique && i.Properties.Count == set.Count && i.Properties.All(set.Contains));
        if (!matches)
            throw new InvalidOperationException(
                $"The conflict target ({string.Join(", ", properties.Select(p => p.Name))}) does not match a declared key " +
                $"or unique index on '{entityType.DisplayName()}'; PostgreSQL requires a unique constraint over the conflict columns.");
    }

    private static async Task<long> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        BulkInsertOptions options,
        string sql,
        CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        if (options.Timeout is { } timeout) command.CommandTimeout = Math.Max(1, (int)timeout.TotalSeconds);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRowsAsync<TEntity>(
        NpgsqlBinaryImporter importer,
        PostgreSqlBulkInsertPlan<TEntity> plan,
        IEnumerable<TEntity> entities,
        CancellationToken cancellationToken)
        where TEntity : class {
        foreach (var entity in entities) {
            GuardEntity(plan, entity);
            await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            foreach (var writer in plan.ColumnWriters)
                await writer(importer, entity, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteRowsAsync<TEntity>(
        NpgsqlBinaryImporter importer,
        PostgreSqlBulkInsertPlan<TEntity> plan,
        IAsyncEnumerable<TEntity> entities,
        CancellationToken cancellationToken)
        where TEntity : class {
        await foreach (var entity in entities.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            GuardEntity(plan, entity);
            await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            foreach (var writer in plan.ColumnWriters)
                await writer(importer, entity, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> OpenConnectionAsync(DbContext context, CancellationToken cancellationToken) {
        if (context.Database.GetDbConnection() is not NpgsqlConnection connection)
            throw new InvalidOperationException(
                "Elarion PostgreSQL bulk operations require the Npgsql provider ('UseNpgsql'); the context's connection is not an NpgsqlConnection.");

        if (connection.State == ConnectionState.Open) return false;

        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void ApplyOptions(NpgsqlBinaryImporter importer, BulkInsertOptions options) {
        if (options.Timeout is { } timeout) importer.Timeout = timeout;
    }

    private static void GuardEntity<TEntity>(PostgreSqlBulkInsertPlan<TEntity> plan, TEntity? entity)
        where TEntity : class {
        if (entity is null) throw new ArgumentException("The entity sequence contains a null element.");

        if (plan.RequiresExactRuntimeType && entity.GetType() != typeof(TEntity))
            throw new NotSupportedException(
                $"Bulk insert into '{typeof(TEntity).Name}' encountered an instance of the derived type '{entity.GetType().Name}', " +
                "whose additional columns the insert would silently drop. Insert derived instances through their own set.");
    }
}
