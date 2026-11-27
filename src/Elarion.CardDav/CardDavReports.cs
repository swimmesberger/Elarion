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

    // RFC 6352 §8.6: the contacts that match the filter. Nothing is indexed (ADR-0113 section 3): every card is read and
    // matched against its property list.
    private async ValueTask<DavResponse?> QueryAsync(DavContext context, DavResource collection, DavXmlElement report,
        CancellationToken cancellationToken) {
        // Evolution sends no filter (sabre/dav accepts that too): every contact matches.
        var filter = report.Child(DavNames.Card.Filter);
        if (filter is not null && CardDavFilterEvaluator.Validate(filter) is { } problem) return DavResponse.Error(403, problem.Condition);
        int? limit = null;
        if (report.Child(DavNames.Card.Limit)?.Child(DavNames.Card.NResults)?.Text.Trim() is { Length: > 0 } nresults) {
            if (!int.TryParse(nresults, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                return DavResponse.Text(400, "CARDDAV:nresults must be a positive integer.");
            limit = parsed;
        }

        var selection = SelectionOf(report);
        if (AsksForUnsupportedAddressData(selection)) return DavResponse.Error(403, SupportedAddressDataCondition);
        var minimal = DavDispatcher.IsMinimal(context.Request);
        var readOptions = options.Value.ContactRead;
        var addressBook = AddressBook(collection);
        // Depth 0 is read as 1, unlike calendar-query: Evolution sends Depth 0 on the address book and expects its members.

        return DavResponse.Xml(207, async (output, ct) => {
            DavMultistatus.Start(output);
            var matched = 0;
            var truncated = false;
            var members = collection.Kind == DavResourceKind.Member
                ? context.Store.GetObjectsAsync(addressBook.Address.Id, [collection.Member!.Name], true, ct)
                : context.Store.GetCandidatesAsync(addressBook.Address.Id, null, null, ct);
            await foreach (var stored in members.ConfigureAwait(false)) {
                if (filter is not null) {
                    using var stream = new MemoryStream(stored.Content.ToArray(), false);
                    var read = await reader.ReadAsync(stream, readOptions, ct).ConfigureAwait(false);
                    if (read.Entries.Count == 0 || !CardDavFilterEvaluator.Matches(read.Entries[0].Properties, filter)) continue;
                }

                if (limit is { } max && matched == max) {
                    truncated = true;
                    break;
                }

                matched++;
                await DavMultistatus.WriteResourceAsync(output, Member(context, collection, stored), selection, minimal, ct).ConfigureAwait(false);
                await output.FlushIfFullAsync(ct).ConfigureAwait(false);
            }

            // RFC 6352 §8.6.1: more matches than the limit are reported as a 507 on the request-URI.
            if (truncated) DavMultistatus.WriteStatus(output, collection.Href, 507);
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
