namespace Elarion.EntityFrameworkCore.Paging;

/// <summary>
/// Declares an ordered keyset (seek) definition for <typeparamref name="TEntity"/> on the annotated
/// partial class, enabling cursor-based pagination. The source generator fills the class with a
/// strongly-typed, reflection-free <see cref="IKeysetDefinition{TEntity}"/> implementation (ordering,
/// seek predicate, and opaque cursor codec) plus a static <c>Definition</c> singleton, so handlers page
/// with <c>source.ToKeysetPageAsync(request, MyKeyset.Definition, selector)</c>.
/// </summary>
/// <remarks>
/// Declare one partial class per ordering, near the feature that pages it — the entity stays free of
/// pagination concerns and may have any number of keyset orderings. List the key columns in order of
/// precedence; columns are ascending by default, and a leading <c>-</c> marks a column descending (e.g.
/// <c>"-CreatedAt"</c>). The final column should be unique (typically the primary key) so the keyset is
/// deterministic; otherwise rows with equal keys may be skipped or repeated across pages.
/// </remarks>
/// <typeparam name="TEntity">The entity type being paginated.</typeparam>
/// <example>
/// <code>
/// // entity stays clean — no pagination concern
/// public sealed class Client
/// {
///     public Guid Id { get; init; }
///     public DateTime CreatedAt { get; init; }
/// }
///
/// // one partial class per ordering; the generator fills it in
/// [Keyset&lt;Client&gt;(nameof(Client.CreatedAt), nameof(Client.Id))]   // newest-last; Id breaks ties
/// public sealed partial class RecentClients;
///
/// // handler
/// var page = await clients.ToKeysetPageAsync(request, RecentClients.Definition, c =&gt; new ClientDto(c.Id));
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class KeysetAttribute<TEntity> : Attribute
    where TEntity : class
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KeysetAttribute{TEntity}"/> class.
    /// </summary>
    /// <param name="columns">
    /// The ordered keyset column names. Each entry names a property on <typeparamref name="TEntity"/>; a
    /// leading <c>-</c> marks the column descending.
    /// </param>
    public KeysetAttribute(params string[] columns)
    {
        Columns = columns;
    }

    /// <summary>
    /// Gets the ordered keyset column names (a leading <c>-</c> marks a descending column).
    /// </summary>
    public IReadOnlyList<string> Columns { get; }
}
