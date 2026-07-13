using System.Collections.Concurrent;
using System.Data.Common;
using Elarion.Abstractions.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Elarion.Idempotency.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="IIdempotencyStore"/> backed by a <typeparamref name="TDbContext"/> whose
/// model includes <see cref="IdempotencyKeyEntity"/> via <c>ApplyElarionIdempotencyKeys</c>.
/// </summary>
/// <remarks>
/// The claim is a change-tracker-free <c>INSERT … ON CONFLICT DO NOTHING</c> run inside the ambient transaction
/// the unit of work opened (so the key row commits atomically with the handler's business writes). The
/// <c>ON CONFLICT</c> form never raises a unique-violation, so the transaction is not poisoned: a returned row is
/// the claim, zero rows means the key already completed (read it and replay), and a PostgreSQL
/// <c>lock_timeout</c> (<c>55P03</c>) while blocked on an in-flight duplicate means "in progress" (409). The
/// unique constraint itself serializes concurrent duplicates across nodes — no external lock. Requires
/// PostgreSQL (the framework's canonical database); other providers do not support the <c>ON CONFLICT</c>
/// claim shape.
/// </remarks>
public sealed class EfCoreIdempotencyStore<TDbContext>(TDbContext dbContext, TimeProvider timeProvider)
    : IIdempotencyStore
    where TDbContext : DbContext {
    private const string PostgresLockNotAvailable = "55P03";

    // Provider- and schema-specific (delimited identifiers, resolved column names), so built once per model.
    private static readonly ConcurrentDictionary<IModel, string> ClaimSqlCache = new();

    /// <inheritdoc />
    public async ValueTask<IdempotencyBeginResult> TryBeginAsync(
        IdempotencyStoreKey key,
        string fingerprint,
        IdempotencyConflictBehavior conflictBehavior,
        CancellationToken ct) {
        var scope = MapScope(key.Scope);
        var now = timeProvider.GetUtcNow();
        var sql = ClaimSqlCache.GetOrAdd(dbContext.Model, static (_, context) => BuildClaimSql(context), dbContext);

        int inserted;
        try {
            object[] parameters = [key.Operation, scope, key.Owner, key.Key, fingerprint, false, false, now, 1];
            inserted = await dbContext.Database.ExecuteSqlRawAsync(sql, parameters, ct).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == PostgresLockNotAvailable) {
            // Blocked on an in-flight duplicate past the lock timeout (Conflict mode) — the winner is running.
            return IdempotencyBeginResult.InProgress();
        }

        // The unit of work applied the short idempotency lock_timeout (SET LOCAL) so only the claim statement
        // above fast-fails/bounds its wait on a concurrent duplicate's row. That timeout must not govern the
        // handler's business statements — an ordinary hot-row wait inside the handler would otherwise surface
        // as 55P03 (a 500). The claim is done, so revert to the session default for the rest of the transaction.
        if (dbContext.Database.CurrentTransaction is not null) {
            await dbContext.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout TO DEFAULT", ct).ConfigureAwait(false);
        }

        if (inserted == 1) {
            return IdempotencyBeginResult.Began();
        }

        // Zero rows: the key already exists and is committed — a pending marker never commits in the
        // single-transaction model, so an existing row is a completed outcome to replay.
        var existing = await dbContext.Set<IdempotencyKeyEntity>()
            .AsNoTracking()
            .Where(entity => entity.Operation == key.Operation && entity.Scope == scope
                && entity.Owner == key.Owner && entity.Key == key.Key)
            .Select(entity => new StoredOutcome(entity.Completed, entity.Fingerprint, entity.Payload, entity.IsFailure))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (existing is not { Completed: true }) {
            // Raced with a concurrent purge/abandon of the same key; let the caller retry as a conflict.
            return IdempotencyBeginResult.InProgress();
        }

        if (fingerprint.Length > 0 && !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)) {
            return IdempotencyBeginResult.FingerprintMismatch();
        }

        return IdempotencyBeginResult.Replay(existing.Payload ?? string.Empty, existing.IsFailure);
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(
        IdempotencyStoreKey key,
        string payload,
        bool isFailure,
        TimeSpan retention,
        CancellationToken ct) {
        var scope = MapScope(key.Scope);
        var now = timeProvider.GetUtcNow();
        var expiresOnUtc = now + retention;

        await dbContext.Set<IdempotencyKeyEntity>()
            .Where(entity => entity.Operation == key.Operation && entity.Scope == scope
                && entity.Owner == key.Owner && entity.Key == key.Key && !entity.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(entity => entity.Completed, true)
                .SetProperty(entity => entity.Payload, payload)
                .SetProperty(entity => entity.IsFailure, isFailure)
                .SetProperty(entity => entity.CompletedOnUtc, now)
                .SetProperty(entity => entity.ExpiresOnUtc, expiresOnUtc)
                .SetProperty(entity => entity.Version, entity => entity.Version + 1), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask AbandonAsync(IdempotencyStoreKey key, CancellationToken ct) =>
        // The pending marker lives in the ambient transaction; rolling the unit of work back discards it, so
        // there is nothing to do here for the EF backend.
        default;

    /// <inheritdoc />
    public async ValueTask<int> PurgeCompletedAsync(DateTimeOffset olderThanUtc, CancellationToken ct) {
        var deleted = await dbContext.Set<IdempotencyKeyEntity>()
            .Where(entity => entity.Completed && entity.ExpiresOnUtc != null && entity.ExpiresOnUtc < olderThanUtc)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        return deleted;
    }

    private static string MapScope(IdempotencyScope scope) =>
        scope switch {
            IdempotencyScope.CurrentUser => "user",
            IdempotencyScope.Consumer => "consumer",
            _ => "global",
        };

    private static string BuildClaimSql(DbContext context) {
        var entityType = context.Model.FindEntityType(typeof(IdempotencyKeyEntity))
            ?? throw new InvalidOperationException(
                "The IdempotencyKeyEntity is not mapped. Call modelBuilder.ApplyElarionIdempotencyKeys() in OnModelCreating.");
        var sqlHelper = context.GetService<ISqlGenerationHelper>();

        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException("The IdempotencyKeyEntity is not mapped to a table.");
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        string Column(string propertyName) {
            var property = entityType.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"The IdempotencyKeyEntity.{propertyName} property is not mapped.");
            var columnName = property.GetColumnName(storeObject)
                ?? throw new InvalidOperationException($"The IdempotencyKeyEntity.{propertyName} property has no column.");
            return sqlHelper.DelimitIdentifier(columnName);
        }

        var table = sqlHelper.DelimitIdentifier(tableName, schema);
        var operationCol = Column(nameof(IdempotencyKeyEntity.Operation));
        var scopeCol = Column(nameof(IdempotencyKeyEntity.Scope));
        var ownerCol = Column(nameof(IdempotencyKeyEntity.Owner));
        var keyCol = Column(nameof(IdempotencyKeyEntity.Key));

        return $"INSERT INTO {table} (" +
            $"{operationCol}, {scopeCol}, {ownerCol}, {keyCol}, {Column(nameof(IdempotencyKeyEntity.Fingerprint))}, " +
            $"{Column(nameof(IdempotencyKeyEntity.Completed))}, {Column(nameof(IdempotencyKeyEntity.IsFailure))}, " +
            $"{Column(nameof(IdempotencyKeyEntity.CreatedOnUtc))}, {Column(nameof(IdempotencyKeyEntity.Version))}) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}) " +
            $"ON CONFLICT ({operationCol}, {scopeCol}, {ownerCol}, {keyCol}) DO NOTHING";
    }

    private sealed record StoredOutcome(bool Completed, string Fingerprint, string? Payload, bool IsFailure);
}
