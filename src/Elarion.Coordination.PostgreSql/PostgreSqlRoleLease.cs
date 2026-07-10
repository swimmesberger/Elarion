using System.Collections.Concurrent;
using Elarion.Abstractions.Coordination;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.Coordination.PostgreSql;

/// <summary>
/// PostgreSQL implementation of <see cref="IRoleLease"/> (ADR-0049): one conditional-upsert row per
/// role — acquisition succeeds when this instance already owns the row or the previous hold has
/// expired, so exactly one instance holds the role at a time and failover is bounded by
/// <see cref="RoleLeaseOptions.LeaseDuration"/>. The <see cref="RoleLeaseHeartbeatService{TDbContext}"/>
/// renews on <see cref="RoleLeaseOptions.RenewInterval"/>.
/// </summary>
/// <remarks>
/// <see cref="IsHeld"/> answers from a locally cached, monotonically-clocked hold-until instant that
/// undershoots the database expiry by <see cref="RoleLeaseOptions.HeldSafetyMargin"/> — this instance
/// stops acting as the holder <em>before</em> another can legitimately take over, so the only
/// double-holding window is a clock-skew/GC-pause pathology, which lease consumers must tolerate
/// (e.g. actors absorb it through snapshot ETags + the transparent conflict retry, ADR-0047). All
/// comparisons use the passed-in application clock, never the database clock.
/// </remarks>
public sealed class PostgreSqlRoleLease<TDbContext>(
    IServiceScopeFactory scopeFactory,
    RoleLeaseOptions options,
    TimeProvider timeProvider,
    ILogger<PostgreSqlRoleLease<TDbContext>> logger) : IRoleLease
    where TDbContext : DbContext {
    // Provider- and schema-specific (delimited identifiers, resolved column names), so built once per model.
    private static readonly ConcurrentDictionary<IModel, string> AcquireSqlCache = new();

    private long _heldUntilTimestamp;
    private volatile string? _currentHolder;

    /// <inheritdoc />
    public string Role => options.RoleName;

    /// <inheritdoc />
    public bool IsHeld => timeProvider.GetTimestamp() < Volatile.Read(ref _heldUntilTimestamp);

    /// <inheritdoc />
    public string? CurrentHolder => _currentHolder;

    /// <summary>
    /// One acquisition/renewal attempt. Returns whether this instance holds the role afterwards.
    /// Called by the heartbeat service; exposed for deterministic tests.
    /// </summary>
    internal async ValueTask<bool> TryAcquireOrRenewAsync(CancellationToken cancellationToken) {
        // Captured BEFORE the database round trip, so the cached hold-until is conservative: the
        // local hold always ends no later than (acquisition start + duration - margin).
        var attemptTimestamp = timeProvider.GetTimestamp();
        var now = timeProvider.GetUtcNow();
        var wasHeld = IsHeld;

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var sql = AcquireSqlCache.GetOrAdd(dbContext.Model, static (_, context) => BuildAcquireSql(context), dbContext);
        var acquired = await dbContext.Database
            .ExecuteSqlRawAsync(
                sql,
                [options.RoleName, options.InstanceId, now + options.LeaseDuration, now],
                cancellationToken)
            .ConfigureAwait(false) == 1;

        if (acquired) {
            var holdFor = options.LeaseDuration - options.HeldSafetyMargin;
            Volatile.Write(
                ref _heldUntilTimestamp,
                attemptTimestamp + (long)(holdFor.TotalSeconds * timeProvider.TimestampFrequency));
            _currentHolder = options.InstanceId;
            if (!wasHeld) {
                logger.LogInformation(
                    "Instance {InstanceId} acquired the role lease '{Role}'.",
                    options.InstanceId, options.RoleName);
            }

            return true;
        }

        Volatile.Write(ref _heldUntilTimestamp, 0);
        _currentHolder = await dbContext.Set<RoleLeaseEntity>()
            .AsNoTracking()
            .Where(entity => entity.Role == options.RoleName)
            .Select(entity => entity.Owner)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (wasHeld) {
            logger.LogWarning(
                "Instance {InstanceId} lost the role lease '{Role}' to {Holder}.",
                options.InstanceId, options.RoleName, _currentHolder);
        }

        return false;
    }

    /// <summary>
    /// Gives the role up (expires our own row) so a shutdown fails over immediately instead of
    /// after <see cref="RoleLeaseOptions.LeaseDuration"/>.
    /// </summary>
    internal async ValueTask ReleaseAsync(CancellationToken cancellationToken) {
        Volatile.Write(ref _heldUntilTimestamp, 0);
        var now = timeProvider.GetUtcNow();
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await dbContext.Set<RoleLeaseEntity>()
            .Where(entity => entity.Role == options.RoleName && entity.Owner == options.InstanceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.ExpiresOnUtc, now), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string BuildAcquireSql(DbContext context) {
        var entityType = context.Model.FindEntityType(typeof(RoleLeaseEntity))
            ?? throw new InvalidOperationException(
                "The RoleLeaseEntity is not mapped. Call modelBuilder.UseElarionRoleLeases() in OnModelCreating "
                + "or annotate the context with [GenerateElarionRoleLeases].");
        var sqlHelper = context.GetService<ISqlGenerationHelper>();

        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException("The RoleLeaseEntity is not mapped to a table.");
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        string Column(string propertyName) {
            var property = entityType.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"The RoleLeaseEntity.{propertyName} property is not mapped.");
            var columnName = property.GetColumnName(storeObject)
                ?? throw new InvalidOperationException($"The RoleLeaseEntity.{propertyName} property has no column.");
            return sqlHelper.DelimitIdentifier(columnName);
        }

        var table = sqlHelper.DelimitIdentifier(tableName, schema);
        var roleCol = Column(nameof(RoleLeaseEntity.Role));
        var ownerCol = Column(nameof(RoleLeaseEntity.Owner));
        var expiresCol = Column(nameof(RoleLeaseEntity.ExpiresOnUtc));

        // Renew when we already own the row, take over when the previous hold expired ({3} = the
        // application clock's now — the database clock is never consulted). One affected row = held.
        // The target row is referenced through an alias: ON CONFLICT's DO UPDATE WHERE cannot use a
        // schema-qualified table name.
        return $"INSERT INTO {table} AS lease ({roleCol}, {ownerCol}, {expiresCol}) " +
            "VALUES ({0}, {1}, {2}) " +
            $"ON CONFLICT ({roleCol}) DO UPDATE SET {ownerCol} = EXCLUDED.{ownerCol}, {expiresCol} = EXCLUDED.{expiresCol} " +
            $"WHERE lease.{ownerCol} = EXCLUDED.{ownerCol} OR lease.{expiresCol} <= {{3}}";
    }
}
