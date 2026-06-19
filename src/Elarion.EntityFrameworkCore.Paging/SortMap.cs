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
///     .CreateBuilder("createdAt", c =&gt; c.CreatedAt)   // the default sort
///     .Add("name", c =&gt; c.Name)
///     .Build();
/// // request.Sort == "-name" sorts by Name descending; unknown keys fall back to the default.
/// </code>
/// </example>
public sealed class SortMap<T>
{
    private readonly FrozenDictionary<string, Func<IQueryable<T>, bool, IOrderedQueryable<T>>> _entries;
    private readonly string _defaultKey;

    internal SortMap(
        string defaultKey,
        FrozenDictionary<string, Func<IQueryable<T>, bool, IOrderedQueryable<T>>> entries)
    {
        _defaultKey = defaultKey;
        _entries = entries;
    }

    /// <summary>
    /// Starts building a sort map whose first entry is the default sort, applied when the request
    /// specifies no key or a key that is not whitelisted. Chain <see cref="SortMapBuilder{T}.Add{TKey}"/>
    /// for each additional allowed key, then call <see cref="SortMapBuilder{T}.Build"/>.
    /// </summary>
    /// <typeparam name="TKey">The type of the sort key.</typeparam>
    /// <param name="key">The default sort key, as clients reference it.</param>
    /// <param name="selector">The key selector for the default sort.</param>
    public static SortMapBuilder<T> CreateBuilder<TKey>(string key, Expression<Func<T, TKey>> selector)
        => new SortMapBuilder<T>(key).Add(key, selector);

    /// <summary>
    /// Applies the sort named by <paramref name="sort"/> (a leading <c>-</c> requests descending),
    /// falling back to the default key (ascending) when the key is blank or not whitelisted.
    /// </summary>
    public IOrderedQueryable<T> Apply(IQueryable<T> source, string? sort)
    {
        ArgumentNullException.ThrowIfNull(source);

        var key = _defaultKey;
        var descending = false;

        if (!string.IsNullOrWhiteSpace(sort))
        {
            var parsed = sort.Trim();
            if (parsed[0] is '-' or '+')
            {
                descending = parsed[0] == '-';
                parsed = parsed[1..];
            }

            if (_entries.ContainsKey(parsed))
            {
                key = parsed;
            }
            else
            {
                descending = false;
            }
        }

        return _entries[key](source, descending);
    }
}
