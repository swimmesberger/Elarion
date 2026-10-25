using System.Reflection;
using System.Runtime.CompilerServices;
using Elarion.Abstractions.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Elarion.EntityFrameworkCore.Lifecycle;

/// <summary>
/// What one model means for lifecycle events: the entity types marked <see cref="PublishLifecycleEventsAttribute"/>
/// (watched), and the cascading foreign keys along which deleting another row reaches one of them. Built once per model.
/// </summary>
/// <remarks>
/// The plan reads the attribute and binds per-entity publishers and queries with reflection over the runtime model
/// (<c>MakeGenericMethod</c>, LINQ built per call), so it is not trimming- or precompiled-query-safe. That matches the EF
/// tier, which is not AOT-compatible; the EF-free SQL tier has no lifecycle events.
/// </remarks>
internal sealed class EntityLifecyclePlan {
    private static readonly ConditionalWeakTable<IModel, EntityLifecyclePlan> Plans = new();

    private static readonly MethodInfo PublishMethod = typeof(EntityLifecyclePlan)
        .GetMethod(nameof(PublishCore), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo DependentIdsMethod = typeof(EntityLifecyclePlan)
        .GetMethod(nameof(DependentIdsCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly Dictionary<Type, Func<IDomainEventBus, IReadOnlyList<Guid>, CancellationToken, ValueTask>>
        _publishers = [];

    private readonly Dictionary<Type, List<CascadeEdge>> _edges = [];
    private readonly Dictionary<Type, IProperty> _keys = [];

    private EntityLifecyclePlan(IModel model) {
        foreach (var entityType in model.GetEntityTypes()) {
            if (entityType.IsOwned() || entityType.HasSharedClrType ||
                entityType.ClrType.GetCustomAttribute<PublishLifecycleEventsAttribute>(false) is null)
                continue;

            _publishers[entityType.ClrType] = PublishMethod.MakeGenericMethod(entityType.ClrType)
                .CreateDelegate<Func<IDomainEventBus, IReadOnlyList<Guid>, CancellationToken, ValueTask>>();
        }

        if (_publishers.Count == 0) return;

        // Every foreign key the database cascades, then the ones whose dependent (transitively) is watched: deleting
        // their principal deletes a watched row the change tracker may never see. A ClientCascade is EF Core's alone: it
        // reaches only tracked rows, which the change tracker reports itself.
        var cascades = model.GetEntityTypes()
            .Where(static entityType => !entityType.IsOwned() && !entityType.HasSharedClrType)
            .SelectMany(static entityType => entityType.GetDeclaredForeignKeys())
            .Where(static fk => fk.DeleteBehavior == DeleteBehavior.Cascade
                                && !fk.PrincipalEntityType.IsOwned() && !fk.PrincipalEntityType.HasSharedClrType)
            .ToList();
        var reaching = new HashSet<Type>(_publishers.Keys);
        bool grew;
        do {
            grew = false;
            foreach (var fk in cascades)
                if (reaching.Contains(fk.DeclaringEntityType.ClrType) && reaching.Add(fk.PrincipalEntityType.ClrType))
                    grew = true;
        } while (grew);

        foreach (var type in reaching) {
            // A row of a hierarchy is tracked as its concrete type and cascades along keys declared on its base, so the
            // per-type bookkeeping below would miss rows; refused until hierarchies are supported.
            var entityType = model.FindEntityType(type)!;
            if (entityType.BaseType is not null || entityType.GetDirectlyDerivedTypes().Any())
                throw new InvalidOperationException(
                    $"{type.Name} {(IsWatched(type) ? "is marked [PublishLifecycleEvents]" : "cascades to an entity marked [PublishLifecycleEvents]")} " +
                    "but is part of an inheritance hierarchy, which lifecycle events do not support.");

            _keys[type] = GuidKey(entityType)
                          ?? throw new InvalidOperationException(
                              $"{type.Name} {(IsWatched(type) ? "is marked [PublishLifecycleEvents]" : "cascades to an entity marked [PublishLifecycleEvents]")} " +
                              "but has no single Guid primary key; lifecycle events identify deleted rows by a Guid id.");
        }

        foreach (var fk in cascades.Where(fk => reaching.Contains(fk.DeclaringEntityType.ClrType))) {
            var dependent = fk.DeclaringEntityType.ClrType;
            if (!fk.PrincipalKey.IsPrimaryKey())
                throw new InvalidOperationException(
                    $"The cascade {fk.PrincipalEntityType.ClrType.Name} → {dependent.Name} reaches an entity marked " +
                    "[PublishLifecycleEvents] through an alternate key; lifecycle events follow cascades by primary key.");

            var foreignKey = fk.Properties.Count == 1 ? fk.Properties[0] : null;
            if (foreignKey is null || (Nullable.GetUnderlyingType(foreignKey.ClrType) ?? foreignKey.ClrType) != typeof(Guid))
                throw new InvalidOperationException(
                    $"The cascade {fk.PrincipalEntityType.ClrType.Name} → {dependent.Name} reaches an entity marked " +
                    "[PublishLifecycleEvents] through a foreign key that is not a single Guid column; lifecycle events " +
                    "cannot follow it.");

            var query = DependentIdsMethod.MakeGenericMethod(dependent)
                .CreateDelegate<Func<DbContext, string, bool, string, List<Guid>, CancellationToken, Task<List<Guid>>>>();
            var edges = _edges.TryGetValue(fk.PrincipalEntityType.ClrType, out var list)
                ? list
                : _edges[fk.PrincipalEntityType.ClrType] = [];
            edges.Add(new CascadeEdge(dependent, foreignKey.Name, foreignKey.ClrType != typeof(Guid),
                _keys[dependent].Name, query));
        }
    }

    /// <summary>Whether the model marks any entity; a plan without one makes the interceptor a no-op.</summary>
    public bool IsEmpty => _publishers.Count == 0;

    public static EntityLifecyclePlan For(IModel model) {
        return Plans.GetValue(model, static m => new EntityLifecyclePlan(m));
    }

    /// <summary>Whether deleting a row of <paramref name="type"/> may delete a watched row, itself or by cascade.</summary>
    public bool IsRelevant(Type type) {
        return _keys.ContainsKey(type);
    }

    public bool IsWatched(Type type) {
        return _publishers.ContainsKey(type);
    }

    public Guid KeyOf(EntityEntry entry) {
        return (Guid)entry.Property(_keys[entry.Metadata.ClrType].Name).CurrentValue!;
    }

    public IReadOnlyList<CascadeEdge> EdgesFrom(Type principal) {
        return _edges.TryGetValue(principal, out var edges) ? edges : [];
    }

    public ValueTask PublishAsync(Type type, IDomainEventBus bus, IReadOnlyList<Guid> ids, CancellationToken ct) {
        return _publishers[type](bus, ids, ct);
    }

    private static IProperty? GuidKey(IEntityType entityType) {
        return entityType.FindPrimaryKey()?.Properties is { Count: 1 } key && key[0].ClrType == typeof(Guid)
            ? key[0]
            : null;
    }

    private static ValueTask PublishCore<TEntity>(IDomainEventBus bus, IReadOnlyList<Guid> ids, CancellationToken ct)
        where TEntity : class {
        return bus.PublishAsync(new EntitiesDeleted<TEntity> { Ids = ids }, ct);
    }

    // Query filters are ignored: a database cascade deletes soft-deleted or other-tenant rows too.
    private static Task<List<Guid>> DependentIdsCoreAsync<TDependent>(
        DbContext db, string foreignKey, bool nullable, string key, List<Guid> principalIds, CancellationToken ct)
        where TDependent : class {
        var rows = db.Set<TDependent>().IgnoreQueryFilters().AsNoTracking();
        if (nullable) {
            var ids = principalIds.Select(static id => (Guid?)id).ToList();
            rows = rows.Where(row => ids.Contains(EF.Property<Guid?>(row, foreignKey)));
        }
        else {
            rows = rows.Where(row => principalIds.Contains(EF.Property<Guid>(row, foreignKey)));
        }

        return rows.Select(row => EF.Property<Guid>(row, key)).ToListAsync(ct);
    }

    /// <summary>A cascading foreign key from a principal to <see cref="Dependent"/>.</summary>
    public sealed record CascadeEdge(
        Type Dependent,
        string ForeignKey,
        bool NullableForeignKey,
        string Key,
        Func<DbContext, string, bool, string, List<Guid>, CancellationToken, Task<List<Guid>>> Query) {
        /// <summary>The ids of the dependents of the given principals.</summary>
        public Task<List<Guid>> DependentIdsAsync(DbContext db, List<Guid> principalIds, CancellationToken ct) {
            return Query(db, ForeignKey, NullableForeignKey, Key, principalIds, ct);
        }
    }
}
