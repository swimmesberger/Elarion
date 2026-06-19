namespace Elarion.EntityFrameworkCore.Paging;

/// <summary>
/// Declares the ordered keyset (seek) columns for an entity, enabling cursor-based pagination.
/// The source generator reads this attribute to emit a strongly-typed, reflection-free keyset
/// definition (ordering, seek predicate, and opaque cursor codec) for the entity.
/// </summary>
/// <remarks>
/// List the key columns in order of precedence. Columns are ascending by default; prefix a column
/// name with <c>-</c> to make it descending (e.g. <c>"-CreatedAt"</c>). The final column should
/// be unique (typically the primary key) so the keyset is deterministic; otherwise rows with equal
/// keys may be skipped or repeated across pages.
/// </remarks>
/// <example>
/// <code>
/// [DbEntity]
/// [Keyset(nameof(CreatedAt), nameof(Id))]   // newest-last; Id breaks ties
/// public sealed class Client
/// {
///     public Guid Id { get; init; }
///     public DateTime CreatedAt { get; init; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class KeysetAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KeysetAttribute"/> class.
    /// </summary>
    /// <param name="columns">
    /// The ordered keyset column names. Each entry names a property on the entity; a leading
    /// <c>-</c> marks the column descending.
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
