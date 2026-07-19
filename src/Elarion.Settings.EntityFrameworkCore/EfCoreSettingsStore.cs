using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Elarion.Settings.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="ISettingsStore"/> backed by a <typeparamref name="TDbContext"/> whose
/// model includes <see cref="Setting"/> via <c>UseElarionSettings</c>.
/// </summary>
/// <remarks>
/// Writes are <b>change-tracker-free and immediate</b> — like the EF Core outbox store, they go through
/// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> (and a raw <c>INSERT</c> for the create path, since EF has no
/// bulk insert), so a settings write never flushes the caller's unrelated tracked changes. Optimistic
/// concurrency is enforced explicitly: an update is guarded on the version observed by a prior read (a lost
/// race affects zero rows and surfaces as <see cref="SettingWriteResult.ConcurrencyConflict"/>), and a
/// create that loses to a concurrent insert is detected by re-checking existence.
/// <para>
/// A write with <c>expectedVersion == null</c> is <b>unconditional (last-write-wins)</b>: the update is keyed
/// only on identity and increments the version in place, so two nodes writing the same key concurrently both
/// succeed rather than one spuriously conflicting. Pass a non-null <c>expectedVersion</c> to opt into the
/// optimistic guard, where a lost race returns <see cref="SettingWriteResult.ConcurrencyConflict"/>.
/// </para>
/// <para>
/// After a successful write the <see cref="IEfCoreSettingsChangeNotifier"/> is signalled with the store's
/// <see cref="DbContext"/>. The default notifier publishes through the in-process
/// <see cref="ISettingsChangePublisher"/>: immediately for a non-transactional write, and — for a write running
/// inside a caller-owned ambient transaction — deferred and announced only when that transaction commits (dropped
/// on rollback, so a phantom notification never fires watchers for a value a rollback discards). A backend-aware
/// notifier (the PostgreSQL <c>LISTEN/NOTIFY</c> source) instead publishes on the store's own connection so the
/// database commit-gates delivery across every node.
/// </para>
/// </remarks>
public sealed class EfCoreSettingsStore<TDbContext>(
    TDbContext dbContext,
    IEfCoreSettingsChangeNotifier changeNotifier,
    TimeProvider timeProvider) : ISettingsStore
    where TDbContext : DbContext {
    // The raw INSERT/UPDATE statements are provider- and schema-specific (delimited identifiers, resolved
    // column names), so they are built once per model and reused.
    private static readonly ConcurrentDictionary<IModel, string> InsertSqlCache = new();
    private static readonly ConcurrentDictionary<IModel, string> UpdateSqlCache = new();

    /// <inheritdoc />
    public async ValueTask<string?> GetAsync(SettingsScope scope, string key,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(key);
        var kind = scope.Kind;
        var owner = scope.Owner ?? string.Empty;

        return await dbContext.Set<Setting>()
            .AsNoTracking()
            .Where(setting => setting.Kind == kind && setting.Owner == owner && setting.Key == key)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SettingEntry>> GetAllAsync(SettingsScope scope,
        CancellationToken cancellationToken = default) {
        var kind = scope.Kind;
        var owner = scope.Owner ?? string.Empty;

        return await dbContext.Set<Setting>()
            .AsNoTracking()
            .Where(setting => setting.Kind == kind && setting.Owner == owner)
            .Select(setting => new SettingEntry(setting.Key, setting.Value, setting.UpdatedOnUtc, setting.Version))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<SettingWriteResult> SetAsync(
        SettingsScope scope,
        string key,
        string? value,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(key);
        var kind = scope.Kind;
        var owner = scope.Owner ?? string.Empty;
        var now = timeProvider.GetUtcNow();

        var current = await dbContext.Set<Setting>()
            .AsNoTracking()
            .Where(setting => setting.Kind == kind && setting.Owner == owner && setting.Key == key)
            .Select(setting => (int?)setting.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current is null) {
            // Create path. expectedVersion 0 (or unset) means "expected absent".
            if (expectedVersion is not null and not 0) return SettingWriteResult.ConcurrencyConflict;

            var raced = false;
            try {
                await InsertAsync(kind, owner, key, value, now, cancellationToken).ConfigureAwait(false);
            }
            catch (DbException) {
                // A concurrent writer may have created the same key first; rethrow if it was some other failure.
                if (!await ExistsAsync(kind, owner, key, cancellationToken).ConfigureAwait(false)) throw;

                // For an explicit "expected absent" (expectedVersion == 0) the concurrent create is a genuine
                // conflict; for the unconditional path (expectedVersion == null) fall through to a last-write-wins
                // update against the row the other writer just created.
                if (expectedVersion is 0) return SettingWriteResult.ConcurrencyConflict;

                raced = true;
            }

            if (!raced) {
                await changeNotifier.NotifyAsync(dbContext, scope, key, cancellationToken).ConfigureAwait(false);
                return SettingWriteResult.Success(1);
            }
        }

        if (current is not null && expectedVersion is { } expected) {
            // Optimistic path: guard the update on the version the caller observed, so a lost race conflicts.
            var observedVersion = current.Value;
            if (expected != observedVersion) return SettingWriteResult.ConcurrencyConflict;

            var guardedNewVersion = observedVersion + 1;
            var guardedUpdated = await dbContext.Set<Setting>()
                .Where(setting => setting.Kind == kind && setting.Owner == owner && setting.Key == key
                                  && setting.Version == observedVersion)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(setting => setting.Value, value)
                    .SetProperty(setting => setting.UpdatedOnUtc, now)
                    .SetProperty(setting => setting.Version, guardedNewVersion), cancellationToken)
                .ConfigureAwait(false);

            if (guardedUpdated == 0)
                // Lost an optimistic race between the read and the conditional update.
                return SettingWriteResult.ConcurrencyConflict;

            await changeNotifier.NotifyAsync(dbContext, scope, key, cancellationToken).ConfigureAwait(false);
            return SettingWriteResult.Success(guardedNewVersion);
        }

        // Unconditional (last-write-wins) path: update keyed only on identity, incrementing Version in place so two
        // nodes writing concurrently both succeed instead of one spuriously conflicting.
        var newVersion = await UnconditionalUpdateAsync(kind, owner, key, value, now, cancellationToken)
            .ConfigureAwait(false);

        if (newVersion is null) {
            // The row was removed by a concurrent delete between the read and this update; recreate it.
            try {
                await InsertAsync(kind, owner, key, value, now, cancellationToken).ConfigureAwait(false);
            }
            catch (DbException) {
                // A concurrent create raced us; if the row now exists, retry the unconditional update once,
                // otherwise the failure is genuine.
                if (!await ExistsAsync(kind, owner, key, cancellationToken).ConfigureAwait(false)) throw;

                newVersion = await UnconditionalUpdateAsync(kind, owner, key, value, now, cancellationToken)
                                 .ConfigureAwait(false)
                             ?? 1;
                await changeNotifier.NotifyAsync(dbContext, scope, key, cancellationToken).ConfigureAwait(false);
                return SettingWriteResult.Success(newVersion.Value);
            }

            await changeNotifier.NotifyAsync(dbContext, scope, key, cancellationToken).ConfigureAwait(false);
            return SettingWriteResult.Success(1);
        }

        await changeNotifier.NotifyAsync(dbContext, scope, key, cancellationToken).ConfigureAwait(false);
        return SettingWriteResult.Success(newVersion.Value);
    }

    /// <summary>
    /// Applies a last-write-wins update keyed only on <c>(Kind, Owner, Key)</c>, incrementing the version column
    /// in place. Returns the new version, or <see langword="null"/> when no row matched (a concurrent delete).
    /// </summary>
    /// <remarks>
    /// Increment-and-read must be a single statement: a separate <c>SELECT</c> after the <c>UPDATE</c> could
    /// observe a concurrent writer's increment and report that writer's version as this caller's own, silently
    /// defeating a subsequent optimistic write. <c>UPDATE … RETURNING</c> reads the row exactly as this
    /// statement produced it.
    /// </remarks>
    private async Task<int?> UnconditionalUpdateAsync(
        string kind, string owner, string key, string? value, DateTimeOffset now, CancellationToken cancellationToken) {
        var sql = UpdateSqlCache.GetOrAdd(dbContext.Model, static (_, context) => BuildUpdateSql(context), dbContext);

        // Same nullable-value handling as the raw INSERT path: a raw null cannot have its store type inferred
        // from a CLR DBNull, so it goes in as an explicitly typed parameter.
        using var parameterFactory = dbContext.Database.GetDbConnection().CreateCommand();
        var valueParameter = parameterFactory.CreateParameter();
        valueParameter.ParameterName = "@p_value";
        valueParameter.DbType = DbType.String;
        valueParameter.Value = (object?)value ?? DBNull.Value;

        object[] parameters = [valueParameter, now, kind, owner, key];
        var versions = await dbContext.Database
            .SqlQueryRaw<int>(sql, parameters)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return versions.Count == 0 ? null : versions[0];
    }

    /// <inheritdoc />
    public async ValueTask<bool> RemoveAsync(
        SettingsScope scope,
        string key,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(key);
        var kind = scope.Kind;
        var owner = scope.Owner ?? string.Empty;

        var deleted = await dbContext.Set<Setting>()
            .Where(setting => setting.Kind == kind && setting.Owner == owner && setting.Key == key
                              && (expectedVersion == null || setting.Version == expectedVersion))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deleted == 0) return false;

        await changeNotifier.NotifyAsync(dbContext, scope, key, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task<bool> ExistsAsync(string kind, string owner, string key, CancellationToken cancellationToken) {
        return dbContext.Set<Setting>()
            .AsNoTracking()
            .AnyAsync(setting => setting.Kind == kind && setting.Owner == owner && setting.Key == key,
                cancellationToken);
    }

    private async Task InsertAsync(string kind, string owner, string key, string? value, DateTimeOffset now,
        CancellationToken cancellationToken) {
        var sql = InsertSqlCache.GetOrAdd(dbContext.Model, static (_, context) => BuildInsertSql(context), dbContext);

        // A raw null cannot have its store type inferred from a CLR DBNull, so the (nullable) value is passed
        // as an explicitly typed parameter; the rest infer cleanly from their runtime types.
        using var parameterFactory = dbContext.Database.GetDbConnection().CreateCommand();
        var valueParameter = parameterFactory.CreateParameter();
        valueParameter.ParameterName = "@p_value";
        valueParameter.DbType = DbType.String;
        valueParameter.Value = (object?)value ?? DBNull.Value;

        object[] parameters = [kind, owner, key, valueParameter, now, 1];
        await dbContext.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildInsertSql(DbContext context) {
        var (sqlHelper, table, column) = ResolveSettingTable(context);
        return $"INSERT INTO {table} (" +
               $"{column(nameof(Setting.Kind))}, {column(nameof(Setting.Owner))}, {column(nameof(Setting.Key))}, " +
               $"{column(nameof(Setting.Value))}, {column(nameof(Setting.UpdatedOnUtc))}, {column(nameof(Setting.Version))}) " +
               "VALUES ({0}, {1}, {2}, {3}, {4}, {5})";
    }

    private static string BuildUpdateSql(DbContext context) {
        var (sqlHelper, table, column) = ResolveSettingTable(context);
        var version = column(nameof(Setting.Version));

        // RETURNING (PostgreSQL, SQLite) atomically reads back the version this statement wrote; the alias
        // "Value" is the column name EF's scalar SqlQueryRaw<T> materializes from.
        return $"UPDATE {table} SET " +
               $"{column(nameof(Setting.Value))} = {{0}}, {column(nameof(Setting.UpdatedOnUtc))} = {{1}}, " +
               $"{version} = {version} + 1 " +
               $"WHERE {column(nameof(Setting.Kind))} = {{2}} AND {column(nameof(Setting.Owner))} = {{3}} " +
               $"AND {column(nameof(Setting.Key))} = {{4}} " +
               $"RETURNING {version} AS {sqlHelper.DelimitIdentifier("Value")}";
    }

    private static (ISqlGenerationHelper SqlHelper, string Table, Func<string, string> Column) ResolveSettingTable(
        DbContext context) {
        var entityType = context.Model.FindEntityType(typeof(Setting))
                         ?? throw new InvalidOperationException(
                             "The Setting entity is not mapped. Call modelBuilder.UseElarionSettings() in OnModelCreating.");
        var sqlHelper = context.GetService<ISqlGenerationHelper>();

        var tableName = entityType.GetTableName()
                        ?? throw new InvalidOperationException("The Setting entity is not mapped to a table.");
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        string Column(string propertyName) {
            var property = entityType.FindProperty(propertyName)
                           ?? throw new InvalidOperationException(
                               $"The Setting.{propertyName} property is not mapped.");
            var columnName = property.GetColumnName(storeObject)
                             ?? throw new InvalidOperationException(
                                 $"The Setting.{propertyName} property has no column.");
            return sqlHelper.DelimitIdentifier(columnName);
        }

        return (sqlHelper, sqlHelper.DelimitIdentifier(tableName, schema), Column);
    }
}
