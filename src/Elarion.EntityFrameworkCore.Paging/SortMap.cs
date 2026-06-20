using System.Collections.Frozen;
using System.Linq.Expressions;

namespace Elarion.EntityFrameworkCore.Paging;

/// <summary>
/// An AOT-safe, immutable whitelist mapping offset-pagination sort keys to typed key selectors. Each
/// key is bound to a strongly-typed <c>OrderBy</c>/<c>OrderByDescending</c> expression, so sorting
/// never uses dynamic LINQ or reflection and clients can only sort by the columns the handler allows.
/// </summary>
/// <remarks>
/// A sort map is a fixed whitelist that does not depend on the request, so build it once with
/// <see cref="CreateBuilder{TKey}(string, Expression{Func{T, TKey}})"/> and reuse the result (for
/// example in a <c>static readonly</c> field). The built map is backed by a
/// <see cref="FrozenDictionary{TKey, TValue}"/> and is immutable, so <see cref="Apply"/> is safe to
/// call concurrently from multiple requests.
/// </remarks>
/// <typeparam name="T">The entity type being sorted.</typeparam>
/// <example>
/// <code>
/// private static readonly SortMap&lt;Client&gt; Sort = SortMap&lt;Client&gt;
///     .CreateBuilder("createdAt", c =&gt; c.CreatedAt, SortDirection.Descending)   // the default sort
///     .ThenBy(c =&gt; c.Id, SortDirection.Descending)                             // stable tiebreaker
///     .Add("name", c =&gt; c.Name)
///     .ThenBy(c =&gt; c.Id)
///     .Build();
/// // request.Sort == "name" sorts by Name then Id; "-createdAt"/"+createdAt" flip the primary column.
/// </code>
/// </example>
public sealed class SortMap<T>
{
    private readonly FrozenDictionary<string, Func<IQueryable<T>, bool?, IOrderedQueryable<T>>> _entries;
    private readonly string _defaultKey;

    internal SortMap(
        string defaultKey,
        FrozenDictionary<string, Func<IQueryable<T>, bool?, IOrderedQueryable<T>>> entries)
    {
        _defaultKey = defaultKey;
        _entries = entries;
    }

    /// <summary>
    /// Starts building a sort map whose first entry is the default sort, applied when the request
    /// specifies no key or a key that is not whitelisted. Chain <see cref="SortMapBuilder{T}.ThenBy{TKey}"/>
    /// to add tiebreakers and <see cref="SortMapBuilder{T}.Add{TKey}"/> for each additional allowed key,
    /// then call <see cref="SortMapBuilder{T}.Build"/>.
    /// </summary>
    /// <typeparam name="TKey">The type of the sort key.</typeparam>
    /// <param name="key">The default sort key, as clients reference it.</param>
    /// <param name="selector">The primary key selector for the default sort.</param>
    /// <param name="direction">The default direction for the primary column.</param>
    public static SortMapBuilder<T> CreateBuilder<TKey>(
        string key, Expression<Func<T, TKey>> selector, SortDirection direction = SortDirection.Ascending)
        => new SortMapBuilder<T>(key).Add(key, selector, direction);

    /// <summary>
    /// Applies the sort named by <paramref name="sort"/>, falling back to the default key when the key is
    /// blank or not whitelisted. A leading <c>-</c> (descending) or <c>+</c> (ascending) flips the
    /// entry's primary column; tiebreakers keep their declared direction. With no prefix the entry's
    /// declared directions are used as-is.
    /// </summary>
    public IOrderedQueryable<T> Apply(IQueryable<T> source, string? sort)
    {
        ArgumentNullException.ThrowIfNull(source);

        var key = _defaultKey;
        bool? primaryDescendingOverride = null;

        if (!string.IsNullOrWhiteSpace(sort))
        {
            var parsed = sort.Trim();
            if (parsed[0] is '-' or '+')
            {
                primaryDescendingOverride = parsed[0] == '-';
                parsed = parsed[1..];
            }

            if (_entries.ContainsKey(parsed))
            {
                key = parsed;
            }
            else
            {
                primaryDescendingOverride = null;
            }
        }

        return _entries[key](source, primaryDescendingOverride);
    }
}
