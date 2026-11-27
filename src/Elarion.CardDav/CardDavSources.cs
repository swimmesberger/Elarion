using Elarion.Abstractions;
using Elarion.WebDav;

namespace Elarion.CardDav;

/// <summary>
/// An address-book source (ADR-0113, spec 0007 FR-011): a query request whose handler returns the address books of the
/// source the current user may see. The handler is an ordinary handler, so <c>[RequirePermission]</c>, resource rules,
/// feature gates and module enablement decide what a contacts app shows exactly as they decide what the application
/// shows. Register it with <c>AddElarionCardDavSource&lt;TSelf&gt;()</c> in the module's <c>ConfigureServices</c>; its
/// handler needs no <c>[Handler]</c>, since it is called in process and is no JSON-RPC operation.
/// </summary>
/// <typeparam name="TSelf">The request type itself.</typeparam>
/// <example>
/// <code>
/// public sealed record CustomerAddressBooks : ICardDavSource&lt;CustomerAddressBooks&gt; {
///     public static string SourceName => "customers";
/// }
///
/// [RequirePermission("customers", Verbs.Read)]
/// public sealed class ListCustomerAddressBooks : IHandler&lt;CustomerAddressBooks, Result&lt;CardDavSourceListing&gt;&gt; {
///     public ValueTask&lt;Result&lt;CardDavSourceListing&gt;&gt; HandleAsync(CustomerAddressBooks query, CancellationToken ct) =&gt;
///         ValueTask.FromResult(Result&lt;CardDavSourceListing&gt;.Success(new CardDavSourceListing([
///             new PublishedAddressBookInfo("all", "Customers"),
///         ])));
/// }
/// </code>
/// </example>
public interface ICardDavSource<TSelf> : IQuery<TSelf, CardDavSourceListing> where TSelf : ICardDavSource<TSelf> {
    /// <summary>
    /// The source's name: 1 to 64 ASCII letters, digits, <c>-</c> and <c>_</c>, unique among the application's
    /// address-book sources (a calendar source may have the same name). It is the first part of each address book's URL
    /// segment, <c>{source}.{key}</c>.
    /// </summary>
    static abstract string SourceName { get; }
}

/// <summary>The address books of a source the current user may see, as the source's handler returns them.</summary>
/// <param name="AddressBooks">The address books; their keys are unique within the source.</param>
public sealed record CardDavSourceListing(IReadOnlyList<PublishedAddressBookInfo> AddressBooks) {
    /// <summary>No address books.</summary>
    public static CardDavSourceListing Empty { get; } = new([]);
}

/// <summary>
/// A published address book as a source describes it to the user who may see it: its key within the source, its name
/// and, optionally, its description.
/// </summary>
/// <param name="Key">The key within the source: 1 to 64 ASCII letters, digits, <c>-</c> and <c>_</c>.</param>
/// <param name="DisplayName">The name contacts apps show.</param>
public sealed record PublishedAddressBookInfo(string Key, string DisplayName) {
    /// <summary>The description (<c>addressbook-description</c>), or <see langword="null"/>.</summary>
    public string? Description { get; init; }
}

/// <summary>The address of a published address book: the source that declares it and its key within the source.</summary>
public sealed record CardDavAddress {
    /// <summary>The longest source name or key: 64 characters.</summary>
    public const int MaxSegmentLength = DavSources.MaxSegmentLength;

    /// <summary>Creates an address.</summary>
    /// <exception cref="ArgumentException">A name is not 1 to 64 ASCII letters, digits, <c>-</c> or <c>_</c>.</exception>
    public CardDavAddress(string source, string key) {
        if (!IsValidSegment(source)) throw new ArgumentException($"The address-book source name '{source}' must be 1 to 64 ASCII letters, digits, '-' or '_'.", nameof(source));
        if (!IsValidSegment(key)) throw new ArgumentException($"The address-book key '{key}' of source '{source}' must be 1 to 64 ASCII letters, digits, '-' or '_'.", nameof(key));
        Source = source;
        Key = key;
    }

    /// <summary>The source name.</summary>
    public string Source { get; }

    /// <summary>The key within the source.</summary>
    public string Key { get; }

    /// <summary>The address in the store.</summary>
    public DavCollectionAddress ToCollectionAddress() => new(CardDavProtocol.Kind, Source, Key);

    /// <inheritdoc />
    public override string ToString() => Source + "." + Key;

    /// <summary>Whether <paramref name="value"/> is 1 to 64 ASCII letters, digits, <c>-</c> or <c>_</c>.</summary>
    public static bool IsValidSegment(string? value) => DavSources.IsValidSegment(value);
}

/// <summary>What the shared source machinery calls an address-book source in its messages.</summary>
internal static class CardDavSourceKind {
    public static DavSourceKind Value { get; } = new("address-book source", "AddElarionCardDavSource");
}
