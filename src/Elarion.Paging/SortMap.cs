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
/// // request.Sort == "name" sorts by Name then Id; "-createdAt"/"+createdAt" flip the primary column;
/// // "+city,-name" sorts by City, then Name descending, then Id (the last key's tiebreaker).
/// </code>
/// </example>
public sealed class SortMap<T> {
    /// <summary>The number of keys a sort may combine unless the builder sets another (<see cref="MaxLevels"/>).</summary>
    public const int DefaultMaxLevels = 3;

    private readonly FrozenDictionary<string, SortEntry<T>> _entries;
    private readonly SortEntry<T> _default;

    internal SortMap(
        string defaultKey,
        FrozenDictionary<string, SortEntry<T>> entries,
        IReadOnlyList<SortKey> keys,
        int maxLevels) {
        _entries = entries;
        _default = entries[defaultKey];
        Keys = keys;
        MaxLevels = maxLevels;
    }

    /// <summary>
    /// The allowed sort keys in declaration order (the default key first), each with the direction a bare key
    /// applies. A client that builds sort parameters reads this — for example to send <c>+key</c> for ascending
    /// on a key whose declared direction is descending — instead of mirroring the declarations by hand.
    /// </summary>
    public IReadOnlyList<SortKey> Keys { get; }

    /// <summary>
    /// The most keys one sort may combine (<c>+city,-name</c> is two), set with
    /// <see cref="SortMapBuilder{T}.MaxLevels"/>; <see cref="DefaultMaxLevels"/> unless set.
    /// </summary>
    public int MaxLevels { get; }

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
    /// Applies the sort named by <paramref name="sort"/>, falling back to the default key when the sort is
    /// blank or not allowed. The sort is one key or a comma-separated list of keys (<c>+city,-name</c>), each
    /// <c>key</c>, <c>+key</c> (ascending) or <c>-key</c> (descending); a <b>bare</b> key applies the key's declared
    /// direction — which is descending for a key declared <see cref="SortDirection.Descending"/>, so a client
    /// asking for ascending must send <c>+key</c>. The keys order from left to right (<c>OrderBy</c>, then
    /// <c>ThenBy</c>), and the declared tiebreakers of the <b>last</b> key follow in their fixed direction, so the
    /// order stays stable. A single key keeps its own tiebreakers, as before.
    /// </summary>
    /// <remarks>
    /// The silent fallback suits a list that must always render. Check the sort with <see cref="Validate"/> first
    /// when an unknown key is a client bug that should be reported rather than masked. A sort is not allowed when a
    /// key is unknown or repeated, or when it combines more than <see cref="MaxLevels"/> keys.
    /// </remarks>
    public IOrderedQueryable<T> Apply(IQueryable<T> source, string? sort) {
        ArgumentNullException.ThrowIfNull(source);

        if (Parse(sort, out var levels) is not null || levels is null) return _default.Apply(source, null);
        if (levels.Length == 1) return levels[0].Entry.Apply(source, levels[0].Descending);

        var ordered = levels[0].Entry.OrderBy(source, levels[0].Descending ?? levels[0].Entry.DeclaredDescending);
        for (var i = 1; i < levels.Length; i++)
            ordered = levels[i].Entry.ThenBy(ordered, levels[i].Descending ?? levels[i].Entry.DeclaredDescending);

        return levels[^1].Entry.ApplyTiebreakers(ordered);
    }

    /// <summary>
    /// Checks that <paramref name="sort"/> is allowed (or is blank, meaning the default sort) and answers a refused one
    /// with a validation failure — the strict counterpart of <see cref="Apply"/>'s silent fallback, for a handler where
    /// a bad sort is a client bug to report rather than mask. Call it before paging.
    /// </summary>
    /// <param name="sort">
    /// The requested sort: one or more comma-separated keys, each <c>key</c>, <c>+key</c> or <c>-key</c>, or blank for
    /// the default.
    /// </param>
    /// <param name="field">
    /// The wire-named request field the sort came from; the failure's field error is keyed by it, so a client marks
    /// that field.
    /// </param>
    /// <param name="message">
    /// Formats the message of a refusal — the hook for an application that shows its own wording or language. Without
    /// it the message is English and names the allowed keys.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when the sort is allowed; otherwise an <see cref="ErrorKind.Validation"/> error with the
    /// code <see cref="PagingErrorCodes.SortKeyUnknown"/> and a field error. Declare it on the handler with
    /// <c>[ProducesError(PagingErrorCodes.SortKeyUnknown, ErrorKind.Validation)]</c>.
    /// </returns>
    /// <example>
    /// <code>
    /// if (Sort.Validate(request.Sort) is { } badSort) return badSort;
    /// return await db.Clients.ToOffsetPageAsync(request, c =&gt; new ClientRow(c.Id, c.Name), Sort, ct: ct);
    /// </code>
    /// </example>
    public AppError? Validate(string? sort, string field = "sort", Func<SortRefusal, string>? message = null) {
        ArgumentNullException.ThrowIfNull(field);
        if (Parse(sort, out _) is not { } refusal) return null;

        var text = message is null ? DefaultMessage(refusal) : message(refusal);
        return AppError.Validation(
            text,
            new Dictionary<string, string[]> { [field] = [text] },
            PagingErrorCodes.SortKeyUnknown);
    }

    private static string DefaultMessage(SortRefusal refusal) {
        var allowed = string.Join(", ", refusal.AllowedKeys.Select(static k => k.Key));
        return refusal.Reason switch {
            SortRefusalReason.RepeatedKey => $"Sort key '{refusal.Key}' is repeated. Allowed keys: {allowed}.",
            SortRefusalReason.TooManyKeys => $"A sort combines at most {refusal.MaxLevels} keys. Allowed keys: {allowed}.",
            _ => $"Unknown sort key '{refusal.Key}'. Allowed keys: {allowed}."
        };
    }

    // Parses a sort into its levels: null levels for a blank sort (the default), else one level per key. Returns the
    // refusal when the sort is not allowed. The level count is checked before any key is looked up, so a hostile sort
    // of many keys costs one pass over the text.
    private SortRefusal? Parse(string? sort, out Level[]? levels) {
        levels = null;
        if (string.IsNullOrWhiteSpace(sort)) return null;

        var count = sort.AsSpan().Count(',') + 1;
        if (count > MaxLevels) return Refuse(SortRefusalReason.TooManyKeys, null);

        var parsed = new Level[count];
        var remaining = sort.AsSpan();
        for (var i = 0; i < count; i++) {
            var comma = remaining.IndexOf(',');
            var token = (comma < 0 ? remaining : remaining[..comma]).Trim();
            remaining = comma < 0 ? [] : remaining[(comma + 1)..];

            bool? descending = null;
            if (token.Length > 0 && token[0] is '-' or '+') {
                descending = token[0] == '-';
                token = token[1..];
            }

            var key = token.ToString();
            if (!_entries.TryGetValue(key, out var entry)) return Refuse(SortRefusalReason.UnknownKey, key);

            for (var j = 0; j < i; j++)
                if (ReferenceEquals(parsed[j].Entry, entry))
                    return Refuse(SortRefusalReason.RepeatedKey, key);

            parsed[i] = new Level(entry, descending);
        }

        levels = parsed;
        return null;
    }

    private SortRefusal Refuse(SortRefusalReason reason, string? key) {
        return new SortRefusal(reason, key, Keys, MaxLevels);
    }

    private readonly record struct Level(SortEntry<T> Entry, bool? Descending);
}

/// <summary>One allowed key of a <see cref="SortMap{T}"/>.</summary>
/// <param name="Key">The key as clients reference it.</param>
/// <param name="Direction">The direction a bare key (no <c>+</c>/<c>-</c> prefix) applies to the primary column.</param>
/// <param name="IsDefault">Whether this is the default sort, applied when no key is requested.</param>
public sealed record SortKey(string Key, SortDirection Direction, bool IsDefault);

/// <summary>Why <see cref="SortMap{T}.Validate"/> refused a sort.</summary>
public enum SortRefusalReason {
    /// <summary>A key the map does not allow (or an empty key, as in <c>name,</c>).</summary>
    UnknownKey = 0,

    /// <summary>A key named twice in one sort.</summary>
    RepeatedKey = 1,

    /// <summary>More keys than <see cref="SortMap{T}.MaxLevels"/>.</summary>
    TooManyKeys = 2
}

/// <summary>A sort that <see cref="SortMap{T}.Validate"/> refused, handed to its message hook.</summary>
/// <param name="Reason">Why the sort was refused.</param>
/// <param name="Key">The key at fault, without its prefix; <see langword="null"/> for <see cref="SortRefusalReason.TooManyKeys"/>.</param>
/// <param name="AllowedKeys">The map's allowed keys (<see cref="SortMap{T}.Keys"/>).</param>
/// <param name="MaxLevels">The most keys a sort may combine (<see cref="SortMap{T}.MaxLevels"/>).</param>
public sealed record SortRefusal(
    SortRefusalReason Reason,
    string? Key,
    IReadOnlyList<SortKey> AllowedKeys,
    int MaxLevels);

/// <summary>The stable error codes of the paging helpers.</summary>
public static class PagingErrorCodes {
    /// <summary>
    /// A sort that the handler's <see cref="SortMap{T}"/> does not allow (<see cref="SortMap{T}.Validate"/>): an unknown
    /// or repeated key, or more keys than the map combines.
    /// </summary>
    public const string SortKeyUnknown = "paging.sort_key_unknown";
}

/// <summary>One key of a built <see cref="SortMap{T}"/>: its primary column as an OrderBy and a ThenBy, and its tiebreakers.</summary>
internal sealed class SortEntry<T>(
    Func<IQueryable<T>, bool, IOrderedQueryable<T>> orderBy,
    Func<IOrderedQueryable<T>, bool, IOrderedQueryable<T>> thenBy,
    bool declaredDescending,
    Func<IOrderedQueryable<T>, IOrderedQueryable<T>>[] tiebreakers) {
    public bool DeclaredDescending { get; } = declaredDescending;

    public IOrderedQueryable<T> OrderBy(IQueryable<T> source, bool descending) {
        return orderBy(source, descending);
    }

    public IOrderedQueryable<T> ThenBy(IOrderedQueryable<T> source, bool descending) {
        return thenBy(source, descending);
    }

    // The key alone: its primary column in the requested (or declared) direction, then its tiebreakers.
    public IOrderedQueryable<T> Apply(IQueryable<T> source, bool? descending) {
        return ApplyTiebreakers(orderBy(source, descending ?? DeclaredDescending));
    }

    public IOrderedQueryable<T> ApplyTiebreakers(IOrderedQueryable<T> ordered) {
        foreach (var tiebreaker in tiebreakers) ordered = tiebreaker(ordered);

        return ordered;
    }
}
