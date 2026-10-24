using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Elarion.Abstractions.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.EntityFrameworkCore.Lifecycle;

/// <summary>
/// Publishes <see cref="EntitiesDeleted{TEntity}"/> for the rows of <see cref="PublishLifecycleEventsAttribute"/>
/// entities a save deletes — tracked deletions and the rows a database cascade will take with them, queried before the
/// <c>DELETE</c> runs — and refuses an <c>ExecuteDelete</c> that would bypass it.
/// </summary>
/// <remarks>
/// <para>
/// Runs in <c>SavingChangesAsync</c>, so consumers act inside the same save and transaction; their own deletions are
/// published in turn, to a fixed point, before EF Core detects changes and builds the save's commands. The synchronous
/// <c>SaveChanges</c> cannot await consumers and is refused while such a deletion is pending.
/// </para>
/// <para>
/// A consumer may save the context itself — typically by running the owning module's delete command. That nested save
/// shares this save's record of what was published, so nothing is published twice and the recursion ends. It flushes
/// the outer deletion early, so it is refused outside a transaction (inside a command, the pipeline's transaction makes
/// it a savepoint).
/// </para>
/// <para>
/// One stateless instance serves every context. That matters: an <see cref="IQueryExpressionInterceptor"/> is a singleton
/// interceptor, part of the key of EF Core's internal service provider, so a fresh instance per scope would build a new
/// internal provider per request. The scoped <see cref="IDomainEventBus"/> comes from the context's application service
/// provider — the scope <c>AddDbContext</c> built the context in.
/// </para>
/// </remarks>
internal sealed class EntityLifecycleInterceptor : SaveChangesInterceptor, IQueryExpressionInterceptor {
    public static readonly EntityLifecycleInterceptor Instance = new();

    // The publication in progress per context, so a consumer's nested save continues it instead of starting over.
    private static readonly ConditionalWeakTable<DbContext, Publication> Active = new();

    private EntityLifecycleInterceptor() { }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) {
        if (eventData.Context is { } db) {
            var plan = EntityLifecyclePlan.For(db.Model);
            if (plan.IsEmpty) return result;

            db.ChangeTracker.CascadeChanges();
            if (db.ChangeTracker.Entries()
                .Any(entry => entry.State == EntityState.Deleted && plan.IsRelevant(entry.Metadata.ClrType)))
                throw new InvalidOperationException(
                    "A row whose deletion publishes lifecycle events is deleted with the synchronous SaveChanges; use " +
                    "SaveChangesAsync so the EntitiesDeleted consumers run in the same unit of work.");
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) {
        if (eventData.Context is { } db) await PublishDeletionsAsync(db, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>Refuses <c>ExecuteDelete</c> on a watched set, or on a set whose deletion cascades to one.</summary>
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData) {
        // The deleted type is ExecuteDelete's own type argument — not the query's root, which a join or a navigation
        // projection makes a different entity.
        if (queryExpression is MethodCallExpression { Method.Name: "ExecuteDelete" or "ExecuteDeleteAsync" } call
            && call.Method.IsGenericMethod
            && eventData.Context?.Model is { } model
            && call.Method.GetGenericArguments()[0] is var deleted
            && EntityLifecyclePlan.For(model).IsRelevant(deleted))
            throw new InvalidOperationException(
                $"ExecuteDelete on {deleted.Name} would delete rows whose deletion publishes lifecycle events without " +
                "publishing them; load the rows, remove them and save with SaveChangesAsync instead.");

        return queryExpression;
    }

    private async Task PublishDeletionsAsync(DbContext db, CancellationToken ct) {
        var plan = EntityLifecyclePlan.For(db.Model);
        if (plan.IsEmpty) return;

        if (Active.TryGetValue(db, out var outer)) {
            if (db.Database.CurrentTransaction is null && System.Transactions.Transaction.Current is null)
                throw new InvalidOperationException(
                    "An EntitiesDeleted consumer saved the context outside a transaction, which would commit the " +
                    "deletion before the remaining consumers ran. Delete inside a command (its pipeline transaction) or " +
                    "a transaction you opened, or let the consumer change the context without saving it.");

            await PublishRoundsAsync(db, plan, outer, ct).ConfigureAwait(false);
            return;
        }

        var publication = new Publication();
        Active.AddOrUpdate(db, publication);
        try {
            await PublishRoundsAsync(db, plan, publication, ct).ConfigureAwait(false);
        }
        finally {
            Active.Remove(db);
        }
    }

    private static async Task PublishRoundsAsync(
        DbContext db, EntityLifecyclePlan plan, Publication publication, CancellationToken ct) {
        var deleted = publication.Deleted;
        var published = publication.Published;
        while (true) {
            // 1. Tracked deletions not seen yet. Deletions EF Core would cascade only at save time
            // (CascadeDeleteTiming/DeleteOrphansTiming.OnSaveChanges) are applied first, and Entries() detects
            // changes, so a deletion made through a navigation (or by a consumer in the previous round) is seen.
            db.ChangeTracker.CascadeChanges();
            var frontier = new Dictionary<Type, List<Guid>>();
            var tracked = new Dictionary<Type, Dictionary<Guid, EntityEntry>>();
            foreach (var entry in db.ChangeTracker.Entries()) {
                var type = entry.Metadata.ClrType;
                if (!plan.IsRelevant(type)) continue;

                var id = plan.KeyOf(entry);
                (tracked.TryGetValue(type, out var entries) ? entries : tracked[type] = [])[id] = entry;
                if (entry.State == EntityState.Deleted && Add(deleted, type, id)) Add(frontier, type, id);
            }

            // 2. What the database will cascade to, queried before the DELETE runs, to a fixed point. A tracked
            // dependent this save moves to another principal survives the cascade, so it is dropped.
            while (frontier.Count > 0) {
                var next = new Dictionary<Type, List<Guid>>();
                foreach (var (principal, ids) in frontier)
                foreach (var edge in plan.EdgesFrom(principal))
                foreach (var id in await edge.DependentIdsAsync(db, ids, ct).ConfigureAwait(false))
                    if (!IsReparented(edge, id, tracked, deleted[principal]) && Add(deleted, edge.Dependent, id))
                        Add(next, edge.Dependent, id);

                frontier = next;
            }

            // 3. Publish what was not published yet, one event per watched type, in a stable order.
            var pending = deleted
                .Where(pair => plan.IsWatched(pair.Key))
                .Select(pair => (Type: pair.Key, Ids: pair.Value
                    .Where(id => !published.TryGetValue(pair.Key, out var done) || !done.Contains(id))
                    .ToList()))
                .Where(static batch => batch.Ids.Count > 0)
                .OrderBy(static batch => batch.Type.FullName, StringComparer.Ordinal)
                .ToList();
            if (pending.Count == 0) return;

            publication.Bus ??= ResolveBus(db);
            foreach (var (type, ids) in pending) {
                foreach (var id in ids) Add(published, type, id);

                await plan.PublishAsync(type, publication.Bus, ids, ct).ConfigureAwait(false);
            }
        }
    }

    // A dependent the database still links to a deleted principal, but that this save updates to point elsewhere: EF Core
    // writes the update before the principal's DELETE, so the cascade no longer reaches it.
    private static bool IsReparented(
        EntityLifecyclePlan.CascadeEdge edge,
        Guid id,
        Dictionary<Type, Dictionary<Guid, EntityEntry>> tracked,
        HashSet<Guid> deletedPrincipals) {
        if (!tracked.TryGetValue(edge.Dependent, out var entries) || !entries.TryGetValue(id, out var entry) ||
            entry.State == EntityState.Deleted)
            return false;

        return entry.Property(edge.ForeignKey).CurrentValue is not Guid principal || !deletedPrincipals.Contains(principal);
    }

    // The bus of the scope the context was resolved in. A context built outside a scope (a factory, a pool, singleton
    // options) carries the root provider, whose bus would hand consumers a different context — their changes would never
    // be saved — so it is refused rather than silently losing the cleanup.
    private static IDomainEventBus ResolveBus(DbContext db) {
        var services = db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;
        if (services is null || !IsResolvedFrom(services, db))
            throw new InvalidOperationException(
                $"Entity lifecycle events need the {db.GetType().Name} resolved from a scope by AddDbContext: consumers " +
                "change the scope's context, so a context from AddDbContextFactory, a pool, or singleton options would " +
                "save without their cleanup.");

        return services.GetService<IDomainEventBus>()
               ?? throw new InvalidOperationException(
                   "Entity lifecycle events need the domain event bus: register it with AddElarionDomainEventBus() " +
                   "(the generated AddElarion does).");
    }

    private static bool IsResolvedFrom(IServiceProvider services, DbContext db) {
        try {
            return ReferenceEquals(services.GetService(db.GetType()), db);
        }
        catch (InvalidOperationException) {
            // Scope validation refuses a scoped context from the root provider: not this scope's context either.
            return false;
        }
    }

    private static bool Add(Dictionary<Type, HashSet<Guid>> map, Type type, Guid id) {
        var set = map.TryGetValue(type, out var existing) ? existing : map[type] = [];
        return set.Add(id);
    }

    private static void Add(Dictionary<Type, List<Guid>> map, Type type, Guid id) {
        (map.TryGetValue(type, out var existing) ? existing : map[type] = []).Add(id);
    }

    // What one outermost save found deleted and published, shared with the nested saves its consumers make.
    private sealed class Publication {
        public Dictionary<Type, HashSet<Guid>> Deleted { get; } = [];

        public Dictionary<Type, HashSet<Guid>> Published { get; } = [];

        public IDomainEventBus? Bus { get; set; }
    }
}
