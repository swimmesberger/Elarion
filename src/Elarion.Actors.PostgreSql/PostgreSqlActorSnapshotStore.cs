using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Actors.PostgreSql;

/// <summary>
/// PostgreSQL implementation of <see cref="IActorSnapshotStore"/> backed by a
/// <typeparamref name="TDbContext"/> whose model includes <see cref="ActorSnapshotEntity"/> via
/// <c>UseElarionActorSnapshots</c>.
/// </summary>
/// <remarks>
/// A singleton that opens a fresh DI scope per operation: actor turns run outside any handler
/// scope, and an activation's own scope lives as long as the activation — far too long to pin a
/// <see cref="DbContext"/>. Every write is change-tracker-free: create is an
/// <c>INSERT … ON CONFLICT DO NOTHING</c> (zero rows = someone else created the snapshot),
/// replace/clear are <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> guarded by the version column, so a
/// stale activation always surfaces as <see cref="ActorSnapshotConcurrencyException"/> instead of a
/// silent lost write. The ETag is the version rendered as invariant text; create mints a
/// lineage-unique random starting version (never <c>1</c>) so a clear + re-create can never
/// resurrect a previously observed version — the seam's lineage-uniqueness requirement. Requires
/// PostgreSQL (the <c>ON CONFLICT</c> create shape and the <c>jsonb</c> payload column).
/// </remarks>
public sealed class PostgreSqlActorSnapshotStore<TDbContext>(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IActorSnapshotStore
    where TDbContext : DbContext {
    // Provider- and schema-specific (delimited identifiers, resolved column names), so built once per model.
    private static readonly ConcurrentDictionary<IModel, string> InsertSqlCache = new();

    /// <inheritdoc />
    public async ValueTask<ActorSnapshot?> ReadAsync(ActorSnapshotKey key, CancellationToken cancellationToken = default) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var row = await dbContext.Set<ActorSnapshotEntity>()
            .AsNoTracking()
            .Where(entity => entity.ActorName == key.ActorName && entity.ActorKey == key.Key)
            .Select(entity => new { entity.State, entity.Version })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : new ActorSnapshot { Payload = row.State, ETag = FormatVersion(row.Version) };
    }

    /// <inheritdoc />
    public async ValueTask<string> WriteAsync(
        ActorSnapshotKey key,
        string payload,
        string? expectedETag,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(payload);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var now = timeProvider.GetUtcNow();

        if (expectedETag is null) {
            var sql = InsertSqlCache.GetOrAdd(dbContext.Model, static (_, context) => BuildInsertSql(context), dbContext);
            var version = MintLineageVersion();
            var inserted = await dbContext.Database
                .ExecuteSqlRawAsync(sql, [key.ActorName, key.Key, payload, now, version], cancellationToken)
                .ConfigureAwait(false);
            if (inserted == 0) {
                throw new ActorSnapshotConcurrencyException(key, expectedETag: null);
            }

            return FormatVersion(version);
        }

        var expectedVersion = ParseVersion(expectedETag);
        var newVersion = expectedVersion + 1;
        var updated = await dbContext.Set<ActorSnapshotEntity>()
            .Where(entity => entity.ActorName == key.ActorName
                             && entity.ActorKey == key.Key
                             && entity.Version == expectedVersion)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(entity => entity.State, payload)
                    .SetProperty(entity => entity.UpdatedOnUtc, now)
                    .SetProperty(entity => entity.Version, newVersion),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated == 0) {
            throw new ActorSnapshotConcurrencyException(key, expectedETag);
        }

        return FormatVersion(newVersion);
    }

    /// <inheritdoc />
    public async ValueTask ClearAsync(ActorSnapshotKey key, string expectedETag, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(expectedETag);
        var expectedVersion = ParseVersion(expectedETag);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var deleted = await dbContext.Set<ActorSnapshotEntity>()
            .Where(entity => entity.ActorName == key.ActorName
                             && entity.ActorKey == key.Key
                             && entity.Version == expectedVersion)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        if (deleted == 0) {
            throw new ActorSnapshotConcurrencyException(key, expectedETag);
        }
    }

    // Create mints a lineage-unique starting version instead of 1: version values must never repeat
    // across create → clear → re-create lineages of one key, otherwise a stale activation's
    // version-guarded write could match a NEW lineage that happens to reach the same number and
    // silently overwrite it — the ABA the seam's "no unnoticed lost write" contract forbids.
    // CSPRNG because this is a correctness guard, not an optimization; 62 random bits leave
    // ~4.6e18 increments of headroom before the bigint could overflow.
    private static long MintLineageVersion() {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        RandomNumberGenerator.Fill(buffer);
        var version = BitConverter.ToInt64(buffer) & (long.MaxValue >> 1);
        return version == 0 ? 1 : version;
    }

    private static string FormatVersion(long version) => version.ToString(CultureInfo.InvariantCulture);

    private static long ParseVersion(string etag) => long.Parse(etag, CultureInfo.InvariantCulture);

    private static string BuildInsertSql(DbContext context) {
        var entityType = context.Model.FindEntityType(typeof(ActorSnapshotEntity))
            ?? throw new InvalidOperationException(
                "The ActorSnapshotEntity is not mapped. Call modelBuilder.UseElarionActorSnapshots() in OnModelCreating "
                + "or annotate the context with [GenerateElarionActorSnapshots].");
        var sqlHelper = context.GetService<ISqlGenerationHelper>();

        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException("The ActorSnapshotEntity is not mapped to a table.");
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        string Column(string propertyName) {
            var property = entityType.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"The ActorSnapshotEntity.{propertyName} property is not mapped.");
            var columnName = property.GetColumnName(storeObject)
                ?? throw new InvalidOperationException($"The ActorSnapshotEntity.{propertyName} property has no column.");
            return sqlHelper.DelimitIdentifier(columnName);
        }

        var table = sqlHelper.DelimitIdentifier(tableName, schema);
        var nameCol = Column(nameof(ActorSnapshotEntity.ActorName));
        var keyCol = Column(nameof(ActorSnapshotEntity.ActorKey));

        // The payload parameter is cast explicitly: an untyped text parameter has no implicit
        // conversion to the jsonb column. ON CONFLICT DO NOTHING keeps a lost create race from
        // raising a unique violation; zero rows is the concurrency signal.
        return $"INSERT INTO {table} (" +
            $"{nameCol}, {keyCol}, {Column(nameof(ActorSnapshotEntity.State))}, " +
            $"{Column(nameof(ActorSnapshotEntity.UpdatedOnUtc))}, {Column(nameof(ActorSnapshotEntity.Version))}) " +
            "VALUES ({0}, {1}, CAST({2} AS jsonb), {3}, {4}) " +
            $"ON CONFLICT ({nameCol}, {keyCol}) DO NOTHING";
    }
}
