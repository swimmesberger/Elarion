using System.Collections.Frozen;
using System.Linq.Expressions;
using Elarion.Abstractions;

namespace Elarion.Paging;

/// <summary>
/// An AOT-safe, immutable whitelist mapping offset-pagination sort keys to typed key selectors. Each
/// key is bound to a strongly-typed <c>OrderBy</c>/<c>OrderByDescending</c> expression, so sorting
/// never uses dynamic LINQ or reflection and clients can only sort by the columns the handler allows.
/// </summary>
/// <remarks>
/// A sort map is a fixed whitelist that does not depend on the request, so build it once with
/// <see cref="CreateBuilder{TKey}(string, Expression{Func{T, TKey}}, SortDirection)"/> and reuse the result (for
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
public sealed class SortMap<T> {
    private readonly FrozenDictionary<string, Func<IQueryable<T>, bool?, IOrderedQueryable<T>>> _entries;
    private readonly string _defaultKey;

    internal SortMap(
        string defaultKey,
        FrozenDictionary<string, Func<IQueryable<T>, bool?, IOrderedQueryable<T>>> entries,
        IReadOnlyList<SortKey> keys) {
        _defaultKey = defaultKey;
        _entries = entries;
        Keys = keys;
    }

    /// <summary>
    /// The allowed sort keys in declaration order (the default key first), each with the direction a bare key
    /// applies. A client that builds sort parameters reads this — for example to send <c>+key</c> for ascending
    /// on a key whose declared direction is descending — instead of mirroring the declarations by hand.
    /// </summary>
    public IReadOnlyList<SortKey> Keys { get; }

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
        string key, Expression<Func<T, TKey>> selector, SortDirection direction = SortDirection.Ascending) {
        return new SortMapBuilder<T>(key).Add(key, selector, direction);
    }

    /// <summary>
    /// Applies the sort named by <paramref name="sort"/>, falling back to the default key when the key is
    /// blank or not whitelisted. A leading <c>-</c> (descending) or <c>+</c> (ascending) sets the
    /// entry's primary column direction; tiebreakers keep their declared direction. A <b>bare</b> key (no
    /// prefix) applies the entry's declared direction — which is descending for a key declared
    /// <see cref="SortDirection.Descending"/>, so a client asking for ascending must send <c>+key</c>.
    /// </summary>
    /// <remarks>
    /// The silent fallback suits a list that must always render. Check the key with <see cref="Validate"/> first
    /// when an unknown key is a client bug that should be reported rather than masked.
    /// </remarks>
    public IOrderedQueryable<T> Apply(IQueryable<T> source, string? sort) {
        ArgumentNullException.ThrowIfNull(source);

        return TryResolve(sort, out var key, out var primaryDescendingOverride)
            ? _entries[key](source, primaryDescendingOverride)
            : _entries[_defaultKey](source, null);
    }

    /// <summary>
    /// Checks that <paramref name="sort"/> names an allowed key (or is blank, meaning the default sort) and answers an
    /// unknown key with a validation failure — the strict counterpart of <see cref="Apply"/>'s silent fallback, for a
    /// handler where an unknown key is a client bug to report rather than mask. Call it before paging.
    /// </summary>
    /// <param name="sort">The requested sort: <c>key</c>, <c>+key</c> or <c>-key</c>, or blank for the default.</param>
    /// <param name="field">
    /// The wire-named request field the sort came from; the failure's field error is keyed by it, so a client marks
    /// that field.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when the key is allowed; otherwise an <see cref="ErrorKind.Validation"/> error with the
    /// code <see cref="PagingErrorCodes.SortKeyUnknown"/> and a field error naming the allowed keys. Declare it on the
    /// handler with <c>[ProducesError(PagingErrorCodes.SortKeyUnknown, ErrorKind.Validation)]</c>.
    /// </returns>
    /// <example>
    /// <code>
    /// if (Sort.Validate(request.Sort) is { } unknownSort) return unknownSort;
    /// return await db.Clients.ToOffsetPageAsync(request, c =&gt; new ClientRow(c.Id, c.Name), Sort, ct: ct);
    /// </code>
    /// </example>
    public AppError? Validate(string? sort, string field = "sort") {
        ArgumentNullException.ThrowIfNull(field);
        if (TryResolve(sort, out _, out _)) return null;

        var requested = sort!.Trim();
        if (requested[0] is '-' or '+') requested = requested[1..];

        var allowed = string.Join(", ", Keys.Select(static k => k.Key));
        return AppError.Validation(
            $"Unknown sort key '{requested}'.",
            new Dictionary<string, string[]> {
                [field] = [$"Unknown sort key '{requested}'. Allowed keys: {allowed}."]
            },
            PagingErrorCodes.SortKeyUnknown);
    }

    // A blank sort resolves to the default key; a whitelisted key (optionally prefixed) resolves to itself; anything
    // else does not resolve.
    private bool TryResolve(string? sort, out string key, out bool? primaryDescendingOverride) {
        key = _defaultKey;
        primaryDescendingOverride = null;
        if (string.IsNullOrWhiteSpace(sort)) return true;

        var parsed = sort.Trim();
        bool? direction = null;
        if (parsed[0] is '-' or '+') {
            direction = parsed[0] == '-';
            parsed = parsed[1..];
        }

        if (!_entries.ContainsKey(parsed)) return false;

        key = parsed;
        primaryDescendingOverride = direction;
        return true;
    }
}

/// <summary>One allowed key of a <see cref="SortMap{T}"/>.</summary>
/// <param name="Key">The key as clients reference it.</param>
/// <param name="Direction">The direction a bare key (no <c>+</c>/<c>-</c> prefix) applies to the primary column.</param>
/// <param name="IsDefault">Whether this is the default sort, applied when no key is requested.</param>
public sealed record SortKey(string Key, SortDirection Direction, bool IsDefault);

/// <summary>The stable error codes of the paging helpers.</summary>
public static class PagingErrorCodes {
    /// <summary>A sort key that the handler's <see cref="SortMap{T}"/> does not allow (<see cref="SortMap{T}.Validate"/>).</summary>
    public const string SortKeyUnknown = "paging.sort_key_unknown";
}
