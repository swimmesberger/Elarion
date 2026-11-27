using System.Text;
using System.Xml;
using Elarion.Contacts;
using Elarion.WebDav;
using Microsoft.Extensions.Options;

namespace Elarion.CardDav;

/// <summary>A published address book the current user sees: its store address and what its source said about it.</summary>
public sealed class CardDavCollection : DavCollection {
    /// <summary>Creates the collection of <paramref name="info"/> at <paramref name="address"/>.</summary>
    public CardDavCollection(CardDavAddress address, PublishedAddressBookInfo info) : base(address.ToCollectionAddress(), info.DisplayName) {
        AddressBookAddress = address;
        Info = info;
    }

    /// <summary>The address book's address.</summary>
    public CardDavAddress AddressBookAddress { get; }

    /// <summary>What the source returned for the address book.</summary>
    public PublishedAddressBookInfo Info { get; }
}

/// <summary>
/// CardDAV on the WebDAV core (RFC 6352, ADR-0113): the address-book home <c>{prefix}/addressbooks/{user}/</c>, the
/// address books the registered sources return for the current user, their properties, and the reports
/// <c>addressbook-multiget</c> and <c>addressbook-query</c>. Every address book is read-only.
/// </summary>
internal sealed partial class CardDavProtocol(DavSourceRegistry<CardDavSourceListing> sources, IOptions<CardDavOptions> options, ContactReader reader)
    : IDavProtocol {
    /// <summary>The collection kind of address books in the store.</summary>
    public const string Kind = "addressbook";

    public string CollectionKind => Kind;

    public string HomeSegment => "addressbooks";

    public string WellKnownName => "carddav";

    // RFC 6352 §6.1; DAVx5 accepts a principal for CardDAV only when OPTIONS on it names this class.
    public IReadOnlyList<string> ComplianceClasses { get; } = ["addressbook"];

    public IReadOnlyList<DavName> CollectionResourceTypes { get; } = [DavNames.Card.AddressBook];

    public IReadOnlyList<DavName> CollectionReports { get; } = [DavNames.Card.AddressBookMultiget, DavNames.Card.AddressBookQuery];

    public IReadOnlyList<DavProperty> PrincipalProperties => field ??= [
        new(DavNames.Card.AddressBookHomeSet, false, null, (w, r, _, _) => {
            DavXml.TextElement(w, DavNames.D.Href, r.Context.HomeHref(this));
            return ValueTask.CompletedTask;
        }),
    ];

    public IReadOnlyList<DavProperty> CollectionProperties => field ??= [
        new(DavNames.Card.AddressBookDescription, true, static r => AddressBook(r).Info.Description is not null, static (w, r, _, _) => {
            w.WriteString(AddressBook(r).Info.Description!);
            return ValueTask.CompletedTask;
        }),
        // RFC 6352 §6.2.2: 3.0 is stored, 4.0 is converted on request (ADR-0113 section 2).
        new(DavNames.Card.SupportedAddressData, false, null, static (w, _, _, _) => {
            foreach (var version in CardDavAddressData.Versions) {
                DavXml.StartElement(w, DavNames.Card.AddressDataType);
                w.WriteAttributeString("content-type", CardDavAddressData.MediaType);
                w.WriteAttributeString("version", version);
                w.WriteEndElement();
            }

            return ValueTask.CompletedTask;
        }),
        new(DavNames.Card.SupportedCollationSet, false, null, static (w, _, _, _) => {
            foreach (var collation in DavTextMatch.Collations) DavXml.TextElement(w, DavNames.Card.SupportedCollation, collation);
            return ValueTask.CompletedTask;
        }),
    ];

    public IReadOnlyList<DavProperty> MemberProperties => field ??= [
        new(DavNames.Card.AddressData, false, static r => r.Content is not null, WriteAddressDataAsync),
    ];

    public async ValueTask<IReadOnlyList<DavCollection>> GetCollectionsAsync(DavContext context, CancellationToken cancellationToken) {
        var collections = new List<DavCollection>();
        foreach (var source in sources.Sources) {
            // A source the user may not use, behind a closed feature gate, or of a disabled module shows no address books.
            var listing = await DavSources.InvokeAsync(source, CardDavSourceKind.Value, context.Services, cancellationToken).ConfigureAwait(false);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var info in listing?.AddressBooks ?? []) {
                if (info is null) continue;
                DavSources.CheckKey(source.Name, info.Key, keys, CardDavSourceKind.Value);
                collections.Add(new CardDavCollection(new CardDavAddress(source.Name, info.Key), info));
            }
        }

        return collections;
    }

    public ValueTask<DavResponse?> ReportAsync(DavContext context, DavResource collection, DavXmlElement report, CancellationToken cancellationToken) {
        if (collection.Kind != DavResourceKind.Collection) return ValueTask.FromResult<DavResponse?>(null);
        if (report.Is(DavNames.Card.AddressBookMultiget)) return MultigetAsync(context, collection, report, cancellationToken);
        if (report.Is(DavNames.Card.AddressBookQuery)) return QueryAsync(context, collection, report, cancellationToken);
        return ValueTask.FromResult<DavResponse?>(null);
    }

    // No feeds: no contacts app subscribes to an address book by URL (ADR-0113 section 4).
    public ValueTask<DavResponse?> HandleOtherAsync(DavContext context, IReadOnlyList<string> segments, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DavResponse?>(null);

    internal static CardDavCollection AddressBook(DavResource resource) => (CardDavCollection)resource.Collection!;

    private async ValueTask WriteAddressDataAsync(XmlWriter writer, DavResource resource, DavXmlElement? requested, CancellationToken cancellationToken) {
        // An address-data the server cannot answer was refused by the report before it wrote anything; where nothing checked
        // it (a PROPFIND, a sync-collection) it falls back to the stored 3.0 card.
        var request = CardDavAddressData.Parse(requested) ?? CardDavAddressData.Stored;
        var bytes = await CardDavAddressData.RenderAsync(resource.Content!.Value, request, reader, options.Value.ContactRead, cancellationToken)
            .ConfigureAwait(false);
        writer.WriteString(Encoding.UTF8.GetString(bytes.Span));
    }
}
