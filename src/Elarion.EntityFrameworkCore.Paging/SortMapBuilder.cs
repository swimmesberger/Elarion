using System.Collections.Frozen;
using System.Linq.Expressions;

namespace Elarion.EntityFrameworkCore.Paging;

/// <summary>
/// Mutable, fluent builder for <see cref="SortMap{T}"/>. Create one with
/// <see cref="SortMap{T}.CreateBuilder{TKey}(string, Expression{Func{T, TKey}}, SortDirection)"/>, add
/// the allowed sort keys (each optionally extended with <see cref="ThenBy{TKey}"/> tiebreakers), then
/// call <see cref="Build"/> to produce an immutable, optimized map.
/// </summary>
/// <remarks>
/// The builder is not thread-safe; populate it on a single thread (typically once at startup) and
/// share the immutable <see cref="SortMap{T}"/> it produces. Each <see cref="Add{TKey}"/> (and the
/// initial <c>CreateBuilder</c>) opens a new sort entry; subsequent <see cref="ThenBy{TKey}"/> calls
/// append fixed-direction tiebreakers to the entry currently being built.
/// </remarks>
/// <typeparam name="T">The entity type being sorted.</typeparam>
public sealed class SortMapBuilder<T>
{
    private readonly Dictionary<string, Func<IQueryable<T>, bool?, IOrderedQueryable<T>>> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _defaultKey;
    private readonly List<Func<IOrderedQueryable<T>, IOrderedQueryable<T>>> _tiebreakers = new();

    private string? _currentKey;
    private Func<IQueryable<T>, bool, IOrderedQueryable<T>>? _currentPrimary;
    private bool _currentPrimaryDescending;

    internal SortMapBuilder(string defaultKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultKey);
        _defaultKey = defaultKey;
    }

    /// <summary>
    /// Opens a new allowed sort entry whose primary column is <paramref name="selector"/>, applied in
    /// <paramref name="direction"/> by default, and returns the builder for chaining. A client may flip
    /// the primary direction at request time with a leading <c>-</c>/<c>+</c>; appended
    /// <see cref="ThenBy{TKey}"/> tiebreakers keep their declared direction.
    /// </summary>
    /// <typeparam name="TKey">The type of the sort key.</typeparam>
    /// <param name="key">The sort key, as clients reference it.</param>
    /// <param name="selector">The primary key selector for this sort.</param>
    /// <param name="direction">The default direction for the primary column.</param>
    public SortMapBuilder<T> Add<TKey>(
        string key, Expression<Func<T, TKey>> selector, SortDirection direction = SortDirection.Ascending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(selector);

        Flush();
        _currentKey = key;
        _currentPrimaryDescending = direction == SortDirection.Descending;
        _currentPrimary = (source, descending) =>
            descending ? source.OrderByDescending(selector) : source.OrderBy(selector);
        return this;
    }

    /// <summary>
    /// Appends a fixed-direction tiebreaker column to the sort entry currently being built, producing a
    /// stable composite order (for example a unique key after a non-unique sort column). Tiebreakers are
    /// not affected by a client's <c>-</c>/<c>+</c> prefix.
    /// </summary>
    /// <typeparam name="TKey">The type of the tiebreaker key.</typeparam>
    /// <param name="selector">The tiebreaker key selector.</param>
    /// <param name="direction">The direction for this tiebreaker column.</param>
    public SortMapBuilder<T> ThenBy<TKey>(
        Expression<Func<T, TKey>> selector, SortDirection direction = SortDirection.Ascending)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (_currentKey is null)
        {
            throw new InvalidOperationException("ThenBy must follow CreateBuilder or Add.");
        }

        var descending = direction == SortDirection.Descending;
        _tiebreakers.Add(ordered =>
            descending ? ordered.ThenByDescending(selector) : ordered.ThenBy(selector));
        return this;
    }

    /// <summary>
    /// Builds an immutable <see cref="SortMap{T}"/> backed by a
    /// <see cref="FrozenDictionary{TKey, TValue}"/>. Further calls on this builder do not affect maps
    /// already built.
    /// </summary>
    public SortMap<T> Build()
    {
        Flush();
        return new(_defaultKey, _entries.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private void Flush()
    {
        if (_currentKey is null)
        {
            return;
        }

        var primary = _currentPrimary!;
        var primaryDeclaredDescending = _currentPrimaryDescending;
        var tiebreakers = _tiebreakers.ToArray();
        _entries[_currentKey] = (source, primaryDescendingOverride) =>
        {
            var descending = primaryDescendingOverride ?? primaryDeclaredDescending;
            var ordered = primary(source, descending);
            foreach (var tiebreaker in tiebreakers)
            {
                ordered = tiebreaker(ordered);
            }

            return ordered;
        };

        _currentKey = null;
        _tiebreakers.Clear();
    }
}
