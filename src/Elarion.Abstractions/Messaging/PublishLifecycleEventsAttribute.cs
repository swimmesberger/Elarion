namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Marks an entity whose lifecycle other modules react to: when a unit of work deletes rows of it — directly or through
/// a database cascade — the EF Core lifecycle interceptor publishes <see cref="EntitiesDeleted{TEntity}"/> on the
/// inline domain-event plane, before the <c>DELETE</c> runs and inside the same transaction.
/// </summary>
/// <remarks>
/// <para>
/// It is the registration of a <b>soft-reference target</b>: data that points at the entity by its id without a foreign
/// key (notes or files attached to records of many kinds) is cleaned up by the module that owns that data, through a
/// <c>[ConsumeEvent]</c> on <see cref="EntitiesDeleted{TEntity}"/> — so the deleting module knows nothing about it, and no
/// delete handler removes it by hand. <c>ExecuteDelete</c> on the entity (or on a set whose deletion cascades to it)
/// is refused, because it would bypass the event.
/// </para>
/// <para>
/// Requires a single <see cref="Guid"/> primary key and an entity outside any inheritance hierarchy (a hierarchy on a
/// cascade path is refused), and takes effect only on a context registered with
/// <c>AddElarionEntityLifecycleEvents&lt;TDbContext&gt;()</c>. Delete inside a transaction (a command): a consumer that
/// saves the context is refused outside one.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [PublishLifecycleEvents]
/// public sealed class Property { public Guid Id { get; init; } … }
///
/// // In the Notes module, which owns notes on records of any kind:
/// [ConsumeEvent]
/// public async ValueTask RemoveNotesAsync(EntitiesDeleted&lt;Property&gt; deleted, CancellationToken ct) {
///     var notes = await db.Notes.Where(n =&gt; deleted.Ids.Contains(n.OwnerId)).ToListAsync(ct);
///     db.Notes.RemoveRange(notes);   // saved by the deleting unit of work's SaveChanges
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PublishLifecycleEventsAttribute : Attribute;

/// <summary>
/// Rows of <typeparamref name="TEntity"/> that the current unit of work deletes: the tracked deletions and the rows a
/// database cascade will take with them, each id published once per save, before the <c>DELETE</c> runs (see
/// <see cref="PublishLifecycleEventsAttribute"/>).
/// </summary>
/// <remarks>
/// A domain event: consumers run inline in the deleting scope and transaction, can still read the rows, and their
/// changes are saved by the same <c>SaveChanges</c>; a consumer failure fails it. A row a consumer deletes is itself
/// published when its type is marked, so cleanup chains (a removed note's files) run to completion.
/// </remarks>
/// <typeparam name="TEntity">The deleted entity type.</typeparam>
public sealed record EntitiesDeleted<TEntity> : IDomainEvent where TEntity : class {
    /// <summary>The primary keys of the deleted rows, each once.</summary>
    public required IReadOnlyList<Guid> Ids { get; init; }
}
