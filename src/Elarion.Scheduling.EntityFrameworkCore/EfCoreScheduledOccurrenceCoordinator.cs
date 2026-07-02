using System.Collections.Concurrent;
using Elarion.Abstractions.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Scheduling.EntityFrameworkCore;

/// <summary>
/// The EF Core/PostgreSQL <see cref="IScheduledOccurrenceCoordinator"/>: claims each recurring occurrence by
/// inserting a <see cref="SchedulerClaimEntity"/> row, so exactly one node in a cluster executes it
/// (ADR-0025). An exact-slot claim (cron — wall-clock deterministic instants) is a plain
/// <c>INSERT … ON CONFLICT DO NOTHING</c> where the composite primary key serializes racers. A window claim
/// (fixed-rate/fixed-delay — node-anchored instants) is a conditional insert that succeeds only when no claim
/// for the job exists within the dedupe window before the due time; because two nodes inserting <i>different</i>
/// instants would both pass a bare <c>NOT EXISTS</c> under read committed, the window path first takes a
/// per-job transactional advisory lock (<c>pg_advisory_xact_lock</c>), which is cheap, self-releasing, and
/// scoped to one job so unrelated jobs never contend.
/// </summary>
/// <typeparam name="TDbContext">The context whose model includes <see cref="SchedulerClaimEntity"/> via <c>UseElarionSchedulerClaims</c>.</typeparam>
public sealed class EfCoreScheduledOccurrenceCoordinator<TDbContext>(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IScheduledOccurrenceCoordinator
    where TDbContext : DbContext {
    // The claim SQL is built from the EF model (not hard-coded identifiers) so the UseElarionSchedulerClaims
    // table/schema overrides and the snake-case toggle apply. Built once per model and reused.
    private static readonly ConcurrentDictionary<IModel, ClaimSql> ClaimSqlCache = new();

    /// <inheritdoc />
    public async ValueTask<bool> TryClaimAsync(ScheduledOccurrence occurrence, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(occurrence.JobName, nameof(occurrence));

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var sql = ClaimSqlCache.GetOrAdd(dbContext.Model, static (_, ctx) => BuildClaimSql(ctx), dbContext);
        var claimedAt = timeProvider.GetUtcNow();

        if (occurrence.DedupeWindow is not { } window) {
            // Exact slot: the primary key is the fence; one racer's insert affects a row, the others' affect none.
            var inserted = await dbContext.Database
                .ExecuteSqlRawAsync(
                    sql.ExactClaim,
                    [occurrence.JobName, occurrence.DueTimeUtc, claimedAt],
                    cancellationToken)
                .ConfigureAwait(false);

            return inserted == 1;
        }

        // Window claim: serialize claimants of this job with a transactional advisory lock, then insert only
        // if no claim exists within the window before the due time.
        var windowStart = occurrence.DueTimeUtc - window;
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await dbContext.Database
            .ExecuteSqlRawAsync(sql.AdvisoryLock, [occurrence.JobName], cancellationToken)
            .ConfigureAwait(false);

        var claimed = await dbContext.Database
            .ExecuteSqlRawAsync(
                sql.WindowClaim,
                [occurrence.JobName, occurrence.DueTimeUtc, claimedAt, windowStart],
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return claimed == 1;
    }

    private sealed record ClaimSql(string ExactClaim, string WindowClaim, string AdvisoryLock);

    private static ClaimSql BuildClaimSql(DbContext context) {
        var entityType = context.Model.FindEntityType(typeof(SchedulerClaimEntity))
            ?? throw new InvalidOperationException(
                "The scheduler claim entity is not mapped. Call modelBuilder.UseElarionSchedulerClaims() in OnModelCreating.");
        var sqlHelper = context.GetService<ISqlGenerationHelper>();

        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException("The scheduler claim entity is not mapped to a table.");
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        string Column(string propertyName) {
            var property = entityType.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"The {nameof(SchedulerClaimEntity)}.{propertyName} property is not mapped.");
            var columnName = property.GetColumnName(storeObject)
                ?? throw new InvalidOperationException($"The {nameof(SchedulerClaimEntity)}.{propertyName} property has no column.");
            return sqlHelper.DelimitIdentifier(columnName);
        }

        var table = sqlHelper.DelimitIdentifier(tableName, schema);
        var jobName = Column(nameof(SchedulerClaimEntity.JobName));
        var occurrence = Column(nameof(SchedulerClaimEntity.OccurrenceUtc));
        var claimedAt = Column(nameof(SchedulerClaimEntity.ClaimedAtUtc));

        return new ClaimSql(
            ExactClaim:
                $"INSERT INTO {table} ({jobName}, {occurrence}, {claimedAt}) VALUES ({{0}}, {{1}}, {{2}}) " +
                $"ON CONFLICT ({jobName}, {occurrence}) DO NOTHING",
            WindowClaim:
                $"INSERT INTO {table} ({jobName}, {occurrence}, {claimedAt}) " +
                $"SELECT {{0}}, {{1}}, {{2}} " +
                $"WHERE NOT EXISTS (SELECT 1 FROM {table} WHERE {jobName} = {{0}} AND {occurrence} > {{3}}) " +
                $"ON CONFLICT ({jobName}, {occurrence}) DO NOTHING",
            AdvisoryLock: "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))");
    }
}
