using System.Collections.Concurrent;
using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Elarion.Authorization.EntityFrameworkCore;

/// <summary>
/// The EF Core backend for <see cref="IResourceGrantStore"/> over the application's
/// <typeparamref name="TDbContext"/>. Grants are idempotent (a duplicate share is a no-op); revocation is a
/// change-tracker-free <c>ExecuteDelete</c>.
/// </summary>
/// <remarks>
/// <para>
/// A grant is a change-tracker-free <c>INSERT … ON CONFLICT (…) DO NOTHING</c> (modeled on
/// <c>EfCoreIdempotencyStore</c>), so a concurrent duplicate never raises a <c>23505</c> unique violation. That
/// matters because <see cref="GrantAsync"/> runs inside the caller's ambient transaction alongside their business
/// writes: a raised unique violation would abort (poison) that whole transaction and fail the later commit, and
/// swallowing it as "success" would also swallow unrelated write errors (e.g. an over-length column). The
/// <c>ON CONFLICT</c> form leaves the transaction usable and reports the desired idempotent outcome without a
/// <c>try/catch</c>. Requires PostgreSQL (the framework's canonical database).
/// </para>
/// <para>
/// The resource-type discriminator, principal ids, and role names are stored and matched
/// <b>case-sensitively</b> (the database's default collation for the equality); a casing mismatch fails closed.
/// </para>
/// </remarks>
/// <typeparam name="TDbContext">The context whose model includes <see cref="ResourceGrantEntity"/>.</typeparam>
internal sealed class EfCoreResourceGrantStore<TDbContext>(TDbContext dbContext) : IResourceGrantStore
    where TDbContext : DbContext {
    // Provider- and schema-specific (delimited identifiers, resolved column names), so built once per model.
    private static readonly ConcurrentDictionary<IModel, string> GrantSqlCache = new();

    public async ValueTask GrantAsync(ResourceGrant grant, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(grant);

        var sql = GrantSqlCache.GetOrAdd(dbContext.Model, static (_, context) => BuildGrantSql(context), dbContext);
        object[] parameters = [
            grant.ResourceType,
            grant.ResourceId,
            grant.Principal.Kind,
            grant.Principal.Id,
            grant.Operation.Name
        ];

        // ON CONFLICT DO NOTHING: a concurrent duplicate is a no-op, never a 23505 that would poison the
        // caller's ambient transaction. Zero rows affected simply means the grant already exists.
        await dbContext.Database.ExecuteSqlRawAsync(sql, parameters, ct).ConfigureAwait(false);
    }

    public async ValueTask RevokeAsync(ResourceGrant grant, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(grant);

        var entity = ToEntity(grant);
        await dbContext.Set<ResourceGrantEntity>()
            .Where(MatchesPredicate(entity))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ResourceGrant>> GetGrantsAsync(
        string resourceType, string resourceId, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(resourceId);

        var rows = await dbContext.Set<ResourceGrantEntity>()
            .AsNoTracking()
            .Where(grant => grant.ResourceType == resourceType && grant.ResourceId == resourceId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(ToContract).ToList();
    }

    private static System.Linq.Expressions.Expression<Func<ResourceGrantEntity, bool>> MatchesPredicate(
        ResourceGrantEntity e) {
        return grant => grant.ResourceType == e.ResourceType
                        && grant.ResourceId == e.ResourceId
                        && grant.PrincipalKind == e.PrincipalKind
                        && grant.PrincipalId == e.PrincipalId
                        && grant.Operation == e.Operation;
    }

    private static ResourceGrantEntity ToEntity(ResourceGrant grant) {
        return new ResourceGrantEntity {
            ResourceType = grant.ResourceType,
            ResourceId = grant.ResourceId,
            PrincipalKind = grant.Principal.Kind,
            PrincipalId = grant.Principal.Id,
            Operation = grant.Operation.Name
        };
    }

    private static ResourceGrant ToContract(ResourceGrantEntity entity) {
        return new ResourceGrant(
            entity.ResourceType,
            entity.ResourceId,
            new ResourcePrincipal(entity.PrincipalKind, entity.PrincipalId),
            new ResourceOperation(entity.Operation));
    }

    private static string BuildGrantSql(DbContext context) {
        var entityType = context.Model.FindEntityType(typeof(ResourceGrantEntity))
                         ?? throw new InvalidOperationException(
                             "The ResourceGrantEntity is not mapped. Call modelBuilder.ApplyElarionResourceGrants() in OnModelCreating.");
        var sqlHelper = context.GetService<ISqlGenerationHelper>();

        var tableName = entityType.GetTableName()
                        ?? throw new InvalidOperationException("The ResourceGrantEntity is not mapped to a table.");
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        string Column(string propertyName) {
            var property = entityType.FindProperty(propertyName)
                           ?? throw new InvalidOperationException(
                               $"The ResourceGrantEntity.{propertyName} property is not mapped.");
            var columnName = property.GetColumnName(storeObject)
                             ?? throw new InvalidOperationException(
                                 $"The ResourceGrantEntity.{propertyName} property has no column.");
            return sqlHelper.DelimitIdentifier(columnName);
        }

        var table = sqlHelper.DelimitIdentifier(tableName, schema);
        var resourceTypeCol = Column(nameof(ResourceGrantEntity.ResourceType));
        var resourceIdCol = Column(nameof(ResourceGrantEntity.ResourceId));
        var principalKindCol = Column(nameof(ResourceGrantEntity.PrincipalKind));
        var principalIdCol = Column(nameof(ResourceGrantEntity.PrincipalId));
        var operationCol = Column(nameof(ResourceGrantEntity.Operation));

        return $"INSERT INTO {table} (" +
               $"{resourceTypeCol}, {resourceIdCol}, {principalKindCol}, {principalIdCol}, {operationCol}) " +
               "VALUES ({0}, {1}, {2}, {3}, {4}) " +
               $"ON CONFLICT ({resourceTypeCol}, {resourceIdCol}, {principalKindCol}, {principalIdCol}, {operationCol}) DO NOTHING";
    }
}
