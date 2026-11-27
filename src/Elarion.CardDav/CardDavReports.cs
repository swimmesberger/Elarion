using Elarion.Contacts;
using Elarion.WebDav;

namespace Elarion.CardDav;

internal sealed partial class CardDavProtocol {
    /// <summary>The <c>supported-address-data</c> precondition of RFC 6352 §8.7: a content type or version the server does not answer.</summary>
    private static readonly DavName SupportedAddressDataCondition = DavNames.Card.SupportedAddressData;

    public async ValueTask<DavRepresentation?> GetRepresentationAsync(DavContext context, DavStoredObject member, CancellationToken cancellationToken) {
        if (CardDavAddressData.PreferredVersion(context.Request.Header("Accept")) != ContactFormatVersion.V4_0) return null;
        var request = new CardDavAddressDataRequest(ContactFormatVersion.V4_0, null);
        var bytes = await CardDavAddressData.RenderAsync(member.Content, request, reader, options.Value.ContactRead, cancellationToken).ConfigureAwait(false);
        return new DavRepresentation(bytes, CardDavAddressData.ContentTypeOf(ContactFormatVersion.V4_0));
    }

    private static DavPropertySelection SelectionOf(DavXmlElement report) {
        if (report.Child(DavNames.D.PropName) is not null) return DavPropertySelection.PropName;
        if (report.Child(DavNames.D.AllProp) is not null) return DavPropertySelection.AllProp;
        return DavPropertySelection.Named(report.Child(DavNames.D.Prop));
    }

    // RFC 6352 §8.7: a report that asks for address-data this server cannot produce is refused as a whole, before any
    // member is written.
    private static bool AsksForUnsupportedAddressData(DavPropertySelection selection) =>
        selection.Requested.Any(static element => element.Is(DavNames.Card.AddressData) && CardDavAddressData.Parse(element) is null);

    // RFC 6352 §8.7: the named contacts, in the order of their hrefs; an href of no contact answers 404.
    private static async ValueTask<DavResponse?> MultigetAsync(DavContext context, DavResource collection, DavXmlElement report,
        CancellationToken cancellationToken) {
        var hrefs = report.ChildrenNamed(DavNames.D.Href).Select(static h => h.Text.Trim()).Where(static h => h.Length > 0).ToList();
        if (hrefs.Count == 0) return DavResponse.Text(400, "An addressbook-multiget needs at least one DAV:href.");
        if (hrefs.Count > context.Options.MaxMultigetHrefs)
            return DavResponse.Text(413, $"An addressbook-multiget may name at most {context.Options.MaxMultigetHrefs} contacts.");

        var selection = SelectionOf(report);
        if (AsksForUnsupportedAddressData(selection)) return DavResponse.Error(403, SupportedAddressDataCondition);
        var withContent = selection.Names(DavNames.Card.AddressData);
        var addressBook = AddressBook(collection);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var href in hrefs)
            if (NameIn(collection.Href, href) is { } name)
                names[href] = name;

        var found = new Dictionary<string, DavStoredObject>(StringComparer.Ordinal);
        if (names.Count > 0)
            await foreach (var stored in context.Store.GetObjectsAsync(addressBook.Address.Id, names.Values.Distinct().ToList(), withContent, cancellationToken)
                               .ConfigureAwait(false))
                found[stored.Name] = stored;

        var minimal = DavDispatcher.IsMinimal(context.Request);
        return DavResponse.Xml(207, async (output, ct) => {
            DavMultistatus.Start(output);
            foreach (var href in hrefs) {
                if (names.TryGetValue(href, out var name) && found.TryGetValue(name, out var stored))
                    await DavMultistatus.WriteResourceAsync(output, Member(context, collection, stored), selection, minimal, ct).ConfigureAwait(false);
                else
                    DavMultistatus.WriteStatus(output, href, 404);
                await output.FlushIfFullAsync(ct).ConfigureAwait(false);
            }

            DavMultistatus.End(output);
        });
    }

    private static DavResource Member(DavContext context, DavResource collection, DavMember member) =>
        new(context, DavResourceKind.Member, context.MemberHref(collection.Protocol!, collection.Collection!, member.Name)) {
            Protocol = collection.Protocol, Collection = collection.Collection, Member = member,
        };

    // The member name an href addresses inside the collection, or null for an href elsewhere. Clients send paths or full
    // URLs, escaped as they like.
    private static string? NameIn(string collectionHref, string href) {
        var path = Uri.TryCreate(href, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" ? absolute.AbsolutePath : href;
        string decodedPath, decodedCollection;
        try {
            decodedPath = Uri.UnescapeDataString(path);
            decodedCollection = Uri.UnescapeDataString(collectionHref);
        }
        catch (UriFormatException) {
            return null;
        }

        if (!decodedPath.StartsWith(decodedCollection, StringComparison.Ordinal)) return null;
        var name = decodedPath[decodedCollection.Length..];
        return name.Length == 0 || name.Contains('/') ? null : name;
    }
}
