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
/// create that loses to a concurrent insert is detected by re-checking existence. After a successful write the
/// supplied <see cref="ISettingsChangePublisher"/> is signalled — with the in-process source this is
/// single-instance notification; a cross-instance change source is a drop-in replacement.
/// </remarks>
public sealed class EfCoreSettingsStore<TDbContext>(
    TDbContext dbContext,
    ISettingsChangePublisher changePublisher,
    TimeProvider timeProvider) : ISettingsStore
    where TDbContext : DbContext {
    // The INSERT statement is provider- and schema-specific (delimited identifiers, resolved column names),
    // so it is built once per model and reused.
    private static readonly ConcurrentDictionary<IModel, string> InsertSqlCache = new();

    /// <inheritdoc />
    public async ValueTask<string?> GetAsync(SettingsScope scope, string key, CancellationToken cancellationToken = default) {
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
    public async ValueTask<IReadOnlyList<SettingEntry>> GetAllAsync(SettingsScope scope, CancellationToken cancellationToken = default) {
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
            if (expectedVersion is not null and not 0) {
                return SettingWriteResult.ConcurrencyConflict;
            }

            try {
                await InsertAsync(kind, owner, key, value, now, cancellationToken).ConfigureAwait(false);
            }
            catch (DbException) {
                // A concurrent writer may have created the same key first; treat that as a conflict, rethrow otherwise.
                if (await ExistsAsync(kind, owner, key, cancellationToken).ConfigureAwait(false)) {
                    return SettingWriteResult.ConcurrencyConflict;
                }

                throw;
            }

            changePublisher.Publish(scope, key);
            return SettingWriteResult.Success(1);
        }

        var observedVersion = current.Value;
        if (expectedVersion is { } expected && expected != observedVersion) {
            return SettingWriteResult.ConcurrencyConflict;
        }

        var newVersion = observedVersion + 1;
        var updated = await dbContext.Set<Setting>()
            .Where(setting => setting.Kind == kind && setting.Owner == owner && setting.Key == key
                && setting.Version == observedVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(setting => setting.Value, value)
                .SetProperty(setting => setting.UpdatedOnUtc, now)
                .SetProperty(setting => setting.Version, newVersion), cancellationToken)
            .ConfigureAwait(false);

        if (updated == 0) {
            // Lost an optimistic race between the read and the conditional update.
            return SettingWriteResult.ConcurrencyConflict;
        }

        changePublisher.Publish(scope, key);
        return SettingWriteResult.Success(newVersion);
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

        if (deleted == 0) {
            return false;
        }

        changePublisher.Publish(scope, key);
        return true;
    }

    private Task<bool> ExistsAsync(string kind, string owner, string key, CancellationToken cancellationToken) =>
        dbContext.Set<Setting>()
            .AsNoTracking()
            .AnyAsync(setting => setting.Kind == kind && setting.Owner == owner && setting.Key == key, cancellationToken);

    private async Task InsertAsync(string kind, string owner, string key, string? value, DateTimeOffset now, CancellationToken cancellationToken) {
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
                ?? throw new InvalidOperationException($"The Setting.{propertyName} property is not mapped.");
            var columnName = property.GetColumnName(storeObject)
                ?? throw new InvalidOperationException($"The Setting.{propertyName} property has no column.");
            return sqlHelper.DelimitIdentifier(columnName);
        }

        var table = sqlHelper.DelimitIdentifier(tableName, schema);
        return $"INSERT INTO {table} (" +
            $"{Column(nameof(Setting.Kind))}, {Column(nameof(Setting.Owner))}, {Column(nameof(Setting.Key))}, " +
            $"{Column(nameof(Setting.Value))}, {Column(nameof(Setting.UpdatedOnUtc))}, {Column(nameof(Setting.Version))}) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5})";
    }
}
