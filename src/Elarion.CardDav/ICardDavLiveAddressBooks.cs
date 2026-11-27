using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Elarion.Contacts;
using Elarion.WebDav;

namespace Elarion.CardDav;

/// <summary>
/// The contacts of a live address-book source, read per request as the current user (ADR-0113, amendment 2026-11-27,
/// spec 0007 FR-035–FR-041). Register it with <c>AddElarionCardDavLiveSource&lt;TQuery, TAddressBooks&gt;()</c>: the
/// source's handler still decides which address books the user sees, through the handler pipeline; this service decides
/// which contacts each of them holds for that user, with the same rules (resource rules, <c>WhereAuthorized</c>) the
/// application's own list queries use. It is resolved from the request's scope, so it may take the <c>DbContext</c> and
/// <c>ICurrentUser</c>.
/// </summary>
/// <remarks>
/// <para>
/// A live address book is answered by its CTag, never by a sync token: <c>sync-collection</c> is not offered on it, so
/// clients compare <see cref="GetCTagAsync"/> and then the entity tags of <see cref="ListAsync"/>, and fetch what changed
/// with <c>addressbook-multiget</c>. The CTag must change whenever the listing would, including when the user's
/// visibility changes; the default computes it from the listing, which is always correct.
/// </para>
/// <para>
/// An entity tag must be the same in the listing and with the contact, and change exactly when the contact's card does
/// (a row version, or a hash of what the card is written from). A member name is the last URL segment of the contact and
/// a URL contract like the address book's segment; <see cref="CardDavAddress.MemberNameOf"/> gives the name a published
/// contact of that UID would have.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// internal sealed class VisibleContacts(AppDbContext db) : ICardDavLiveAddressBooks {
///     public IAsyncEnumerable&lt;CardDavLiveMember&gt; ListAsync(string key, CancellationToken ct) =&gt;
///         db.Contacts.WhereAuthorized(Verbs.Read).Select(c =&gt; new CardDavLiveMember(c.Id + ".vcf", c.Version.ToString())).AsAsyncEnumerable();
///
///     public async IAsyncEnumerable&lt;CardDavLiveContact&gt; GetAsync(string key, IReadOnlyCollection&lt;string&gt;? names,
///         DavContentPurpose purpose, [EnumeratorCancellation] CancellationToken ct) {
///         // ... the visible contacts named (all when names is null); photos only when purpose is not Query
///     }
/// }
/// </code>
/// </example>
public interface ICardDavLiveAddressBooks {
    /// <summary>The name and entity tag of every contact the current user sees in address book <paramref name="key"/>.</summary>
    IAsyncEnumerable<CardDavLiveMember> ListAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// The contacts of address book <paramref name="key"/> named <paramref name="names"/> that the current user sees, or
    /// all of them when <paramref name="names"/> is <see langword="null"/> (an <c>addressbook-query</c>, or a
    /// <c>PROPFIND</c> listing that names <c>address-data</c>, with <see cref="DavContentPurpose.Multiget"/>).
    /// <paramref name="purpose"/> is <see cref="DavContentPurpose.Get"/>, <see cref="DavContentPurpose.Multiget"/> or
    /// <see cref="DavContentPurpose.Query"/>: a query matches and answers every contact, so it may leave out what no
    /// search reads, such as photos; the others answer the card a client keeps under its entity tag, so they leave
    /// nothing out. One call answers one response, so a byte budget for photos is kept within it.
    /// </summary>
    IAsyncEnumerable<CardDavLiveContact> GetAsync(string key, IReadOnlyCollection<string>? names, DavContentPurpose purpose,
        CancellationToken cancellationToken);

    /// <summary>
    /// The CTag of address book <paramref name="key"/> for the current user. Default: a hash over the names and entity tags
    /// of <see cref="ListAsync"/>, which changes exactly when the listing does; override it with a cheaper value that
    /// changes at least as often.
    /// </summary>
    async ValueTask<string> GetCTagAsync(string key, CancellationToken cancellationToken) {
        var members = new List<CardDavLiveMember>();
        await foreach (var member in ListAsync(key, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false)) members.Add(member);
        return CardDavLiveMember.ComputeCTag(members);
    }
}

/// <summary>A contact of a live address book as its listing names it: its member name and its entity tag.</summary>
/// <param name="Name">
/// The member name, the last segment of the contact's URL, such as <c>42.vcf</c>: 1 to 128 ASCII letters, digits,
/// <c>-</c>, <c>_</c>, <c>.</c>, <c>~</c> or <c>@</c>.
/// </param>
/// <param name="ETag">The strong entity tag; quotes are added when it has none.</param>
public sealed record CardDavLiveMember(string Name, string ETag) {
    /// <summary>
    /// The CTag of a listing: a hash over its names and entity tags in name order, so it changes exactly when a contact is
    /// added, changed or no longer seen.
    /// </summary>
    public static string ComputeCTag(IEnumerable<CardDavLiveMember> members) {
        ArgumentNullException.ThrowIfNull(members);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var member in members.OrderBy(static m => m.Name, StringComparer.Ordinal)) {
            hash.AppendData(Encoding.UTF8.GetBytes(member.Name));
            hash.AppendData("\n"u8);
            hash.AppendData(Encoding.UTF8.GetBytes(member.ETag));
            hash.AppendData("\n"u8);
        }

        return "\"" + Convert.ToBase64String(hash.GetHashAndReset()[..16]).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "\"";
    }
}

/// <summary>
/// A contact of a live address book with its card: its member name, its entity tag and either the contact, which the
/// writer turns into a card in its own order, or the card's property list, written in the order and spelling it holds.
/// </summary>
/// <example>
/// <code>
/// // The application's own wire format: the writer's properties, reordered, and an extra one.
/// var card = contact.ToProperties(ContactFormatVersion.V3_0).OrderBy(Rank).Append(new ContactProperty("X-SOURCE", "crm")).ToList();
/// yield return new CardDavLiveContact(name, etag, card);
/// </code>
/// </example>
public sealed record CardDavLiveContact {
    /// <summary>A live contact written from <paramref name="contact"/>.</summary>
    /// <param name="name">The member name, as <see cref="ICardDavLiveAddressBooks.ListAsync"/> names it.</param>
    /// <param name="etag">The entity tag, as the listing gives it.</param>
    /// <param name="contact">The contact, written as vCard 3.0 like a published one: it needs a UID and a name to show.</param>
    public CardDavLiveContact(string name, string etag, Contact contact) {
        ArgumentNullException.ThrowIfNull(contact);
        Name = name;
        ETag = etag;
        Contact = contact;
    }

    /// <summary>
    /// A live contact written from the property list <paramref name="card"/>, as
    /// <c>ContactWriter.AddAsync(IReadOnlyList&lt;ContactProperty&gt;)</c> writes it: in its order and spelling, as vCard 3.0
    /// (a <c>VERSION</c> property is replaced). It needs a <c>UID</c> and an <c>FN</c>, and its values are escaped as
    /// a read entry's are.
    /// </summary>
    /// <param name="name">The member name, as <see cref="ICardDavLiveAddressBooks.ListAsync"/> names it.</param>
    /// <param name="etag">The entity tag, as the listing gives it.</param>
    /// <param name="card">The properties, without <c>BEGIN</c> and <c>END</c>.</param>
    public CardDavLiveContact(string name, string etag, IReadOnlyList<ContactProperty> card) {
        ArgumentNullException.ThrowIfNull(card);
        Name = name;
        ETag = etag;
        Card = card;
    }

    /// <summary>The member name, the last segment of the contact's URL.</summary>
    public string Name { get; }

    /// <summary>The entity tag, as the listing gives it.</summary>
    public string ETag { get; }

    /// <summary>The contact, or <see langword="null"/> when the card is given as <see cref="Card"/>.</summary>
    public Contact? Contact { get; }

    /// <summary>The card's property list, or <see langword="null"/> when it is written from <see cref="Contact"/>.</summary>
    public IReadOnlyList<ContactProperty>? Card { get; }
}

/// <summary>A registered live address-book source: its name and how to resolve its contacts in a request's scope.</summary>
internal sealed record CardDavLiveSourceRegistration(string Name, Func<IServiceProvider, ICardDavLiveAddressBooks> Resolve);

/// <summary>A live address book as the WebDAV core reads it: the application's contacts, written as vCard 3.0 per request.</summary>
internal sealed class CardDavLiveCollection(ICardDavLiveAddressBooks books, string key, ContactReadOptions readOptions) : IDavLiveCollection {
    public ValueTask<string> GetCTagAsync(DavContext context, CancellationToken cancellationToken) => books.GetCTagAsync(key, cancellationToken);

    public async IAsyncEnumerable<DavMember> ListMembersAsync(DavContext context, [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var member in books.ListAsync(key, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false)) {
            yield return new DavMember { Name = CheckName(member.Name), ETag = Quote(member.ETag), ContentType = CardDavObjects.ContentType };
        }
    }

    public async IAsyncEnumerable<DavStoredObject> GetMembersAsync(DavContext context, IReadOnlyCollection<string>? names, DavContentPurpose purpose,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        var wanted = names is null ? null : new HashSet<string>(names, StringComparer.Ordinal);
        if (purpose == DavContentPurpose.Metadata) {
            // No card is written for a property request: the listing answers it.
            await foreach (var member in ListMembersAsync(context, cancellationToken).ConfigureAwait(false)) {
                if (wanted is not null && !wanted.Contains(member.Name)) continue;
                yield return new DavStoredObject {
                    Name = member.Name, ETag = member.ETag, ContentType = member.ContentType, Uid = member.Name, Content = ReadOnlyMemory<byte>.Empty,
                };
            }

            yield break;
        }

        await foreach (var live in books.GetAsync(key, names, purpose, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false)) {
            if (live is null || (wanted is not null && !wanted.Contains(live.Name))) continue;
            var content = live.Card is { } card
                ? await CardDavObjects.ToContentAsync(card, key, readOptions, cancellationToken).ConfigureAwait(false)
                : await CardDavObjects.ToContentAsync(live.Contact!, readOptions, cancellationToken).ConfigureAwait(false);
            yield return new DavStoredObject {
                Name = CheckName(live.Name),
                Uid = content.Uid,
                ETag = Quote(live.ETag),
                ContentType = CardDavObjects.ContentType,
                Size = content.Content.Length,
                Content = content.Content,
            };
        }
    }

    private string CheckName(string name) => DavCollection.IsValidSegment(name)
        ? name
        : throw new InvalidOperationException(
            $"The live address book '{key}' named a contact '{name}'; a member name must be 1 to {DavCollection.MaxSegmentLength} ASCII letters, "
            + "digits, '-', '_', '.', '~' or '@'.");

    private string Quote(string etag) {
        if (string.IsNullOrEmpty(etag) || etag == "\"\"")
            throw new InvalidOperationException($"The live address book '{key}' gave an empty entity tag.");
        var quoted = etag.Length >= 2 && etag[0] == '"' && etag[^1] == '"' ? etag : "\"" + etag + "\"";
        foreach (var c in quoted.AsSpan(1, quoted.Length - 2))
            if (c is < '!' or > '~' or '"')
                throw new InvalidOperationException(
                    $"The live address book '{key}' gave the entity tag '{etag}'; an entity tag is printable ASCII without spaces or quotes.");
        return quoted;
    }
}
