using System.Linq.Expressions;

namespace Elarion.EntityFrameworkCore.Paging;

/// <summary>
/// An AOT-safe whitelist mapping offset-pagination sort keys to typed key selectors. Each key is
/// bound to a strongly-typed <c>OrderBy</c>/<c>OrderByDescending</c> expression, so sorting never
/// uses dynamic LINQ or reflection and clients can only sort by the columns the handler allows.
/// </summary>
/// <typeparam name="T">The entity type being sorted.</typeparam>
/// <example>
/// <code>
/// var sort = SortMap&lt;Client&gt;
///     .Create("createdAt", c =&gt; c.CreatedAt)   // the default sort
///     .Add("name", c =&gt; c.Name);
/// // request.Sort == "-name" sorts by Name descending; unknown keys fall back to the default.
/// </code>
/// </example>
public sealed class SortMap<T>
{
    private readonly Dictionary<string, Func<IQueryable<T>, bool, IOrderedQueryable<T>>> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _defaultKey;

    private SortMap(string defaultKey)
    {
        _defaultKey = defaultKey;
    }

    /// <summary>Creates a sort map whose first entry is the default (applied when no/unknown sort is requested).</summary>
    /// <typeparam name="TKey">The type of the sort key.</typeparam>
    /// <param name="key">The default sort key, as clients reference it.</param>
    /// <param name="selector">The key selector for the default sort.</param>
    public static SortMap<T> Create<TKey>(string key, Expression<Func<T, TKey>> selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(selector);
        var map = new SortMap<T>(key);
        map.Add(key, selector);
        return map;
    }

    /// <summary>Adds an allowed sort key bound to <paramref name="selector"/>.</summary>
    /// <typeparam name="TKey">The type of the sort key.</typeparam>
    /// <param name="key">The sort key, as clients reference it.</param>
    /// <param name="selector">The key selector for this sort.</param>
    public SortMap<T> Add<TKey>(string key, Expression<Func<T, TKey>> selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(selector);
        _entries[key] = (source, descending) =>
            descending ? source.OrderByDescending(selector) : source.OrderBy(selector);
        return this;
    }

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
