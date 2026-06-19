using System.Collections.Frozen;
using System.Linq.Expressions;

namespace Elarion.EntityFrameworkCore.Paging;

/// <summary>
/// Mutable, fluent builder for <see cref="SortMap{T}"/>. Create one with
/// <see cref="SortMap{T}.CreateBuilder{TKey}(string, Expression{Func{T, TKey}})"/>, add the allowed
/// sort keys, then call <see cref="Build"/> to produce an immutable, optimized map.
/// </summary>
/// <remarks>
/// The builder is not thread-safe; populate it on a single thread (typically once at startup) and
/// share the immutable <see cref="SortMap{T}"/> it produces.
/// </remarks>
/// <typeparam name="T">The entity type being sorted.</typeparam>
public sealed class SortMapBuilder<T>
{
    private readonly Dictionary<string, Func<IQueryable<T>, bool, IOrderedQueryable<T>>> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _defaultKey;

    internal SortMapBuilder(string defaultKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultKey);
        _defaultKey = defaultKey;
    }

    /// <summary>Adds an allowed sort key bound to <paramref name="selector"/> and returns the builder for chaining.</summary>
    /// <typeparam name="TKey">The type of the sort key.</typeparam>
    /// <param name="key">The sort key, as clients reference it.</param>
    /// <param name="selector">The key selector for this sort.</param>
    public SortMapBuilder<T> Add<TKey>(string key, Expression<Func<T, TKey>> selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(selector);
        _entries[key] = (source, descending) =>
            descending ? source.OrderByDescending(selector) : source.OrderBy(selector);
        return this;
    }

    /// <summary>
    /// Builds an immutable <see cref="SortMap{T}"/> backed by a
    /// <see cref="FrozenDictionary{TKey, TValue}"/>. Further <see cref="Add{TKey}"/> calls on this
    /// builder do not affect maps already built.
    /// </summary>
    public SortMap<T> Build()
        => new(_defaultKey, _entries.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
}
