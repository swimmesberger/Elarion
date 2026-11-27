using System.Globalization;
using System.Text;
using Elarion.ContentLines;

namespace Elarion.Contacts.Writing;

/// <summary>
/// Turns a contact into the properties of a vCard 3.0 or 4.0 card, in the writer's fixed order (FR-052–FR-060, FR-064,
/// FR-067): the form each version's importers read.
/// </summary>
internal static class ContactSerializer {
    private static readonly (ContactTypes Flag, string Name)[] TypeNames = [
        (ContactTypes.Home, "HOME"), (ContactTypes.Work, "WORK"), (ContactTypes.Cell, "CELL"), (ContactTypes.Voice, "VOICE"),
        (ContactTypes.Fax, "FAX"), (ContactTypes.Pager, "PAGER"), (ContactTypes.Text, "TEXT"), (ContactTypes.Video, "VIDEO"),
        (ContactTypes.Main, "MAIN"),
    ];

    public static IReadOnlyList<ContactProperty> ToProperties(Contact contact, ContactFormatVersion version) {
        ArgumentNullException.ThrowIfNull(contact);
        var v3 = version == ContactFormatVersion.V3_0;
        var list = new List<ContactProperty>();
        var groups = new Groups(contact.AdditionalProperties);

        if (contact.Kind == ContactKind.Organization)
            list.Add(v3 ? new ContactProperty("X-ABSHOWAS", "COMPANY") : new ContactProperty("KIND", "org"));
        // FR-070: each version in the form its clients read, never the other's (ADR-0114).
        var isGroup = contact.Kind == ContactKind.Group;
        if (isGroup) list.Add(v3 ? new ContactProperty("X-ADDRESSBOOKSERVER-KIND", "group") : new ContactProperty("KIND", "group"));
        else if (contact.Members.Count > 0)
            throw new ContactWriteException("Only a group has members; set Kind to ContactKind.Group or remove the members.", null, "MEMBER");

        var formattedName = string.IsNullOrWhiteSpace(contact.FormattedName) ? contact.DisplayName : contact.FormattedName;
        list.Add(new ContactProperty("FN", ContentText.Escape(formattedName)));

        if (contact.Name is not null || v3) {
            var n = contact.Name;
            // A 3.0 group without a name carries its own as the family name, as Apple and Cyrus write it.
            var family = n?.Family ?? (isGroup && n is null ? formattedName : null);
            list.Add(new ContactProperty("N", Structured(family, n?.Given, n?.Additional, n?.Prefix, n?.Suffix)));
        }

        if (contact.Nicknames.Count > 0) list.Add(new ContactProperty("NICKNAME", List(contact.Nicknames)));
        if (contact.Organization is not null || contact.Department is not null) {
            var org = ContentText.Escape(contact.Organization ?? "");
            if (contact.Department is not null) org += ";" + ContentText.Escape(contact.Department);
            list.Add(new ContactProperty("ORG", org));
        }

        if (contact.Title is not null) list.Add(new ContactProperty("TITLE", ContentText.Escape(contact.Title)));
        if (contact.Role is not null) list.Add(new ContactProperty("ROLE", ContentText.Escape(contact.Role)));

        foreach (var phone in contact.Phones)
            AddTyped(list, groups, "TEL", ContentText.Escape(phone.Number), phone.Types, phone.Preferred, phone.Label, v3);
        foreach (var email in contact.Emails)
            AddTyped(list, groups, "EMAIL", ContentText.Escape(email.Address), email.Types, email.Preferred, email.Label, v3);
        foreach (var address in contact.Addresses) {
            var value = Structured(address.PostOfficeBox, address.Extended, address.Street, address.Locality, address.Region, address.PostalCode, address.Country);
            AddTyped(list, groups, "ADR", value, address.Types, address.Preferred, address.Label, v3);
        }

        foreach (var url in contact.Urls)
            AddTyped(list, groups, "URL", ContentText.Escape(url.Value), url.Types, url.Preferred, url.Label, v3);

        if (contact.Birthday is { } birthday) list.Add(Date(null, "BDAY", birthday, v3));
        if (contact.Anniversary is { } anniversary) {
            if (v3) {
                // Apple reads its labelled X-ABDATE, other 3.0 importers X-ANNIVERSARY; the reader takes either.
                list.Add(Date(null, "X-ANNIVERSARY", anniversary, v3));
                var group = groups.Next();
                list.Add(Date(group, "X-ABDATE", anniversary, v3));
                list.Add(new ContactProperty(group, "X-ABLabel", [], "_$!<Anniversary>!$_"));
            }
            else {
                list.Add(Date(null, "ANNIVERSARY", anniversary, v3));
            }
        }

        if (contact.Note is not null) list.Add(new ContactProperty("NOTE", ContentText.Escape(contact.Note)));
        if (contact.Categories.Count > 0) list.Add(new ContactProperty("CATEGORIES", List(contact.Categories)));
        if (contact.Photo is { } photo) list.Add(Photo(photo, v3));
        if (contact.Uid is not null) list.Add(new ContactProperty("UID", ContentText.Escape(contact.Uid)));
        foreach (var member in contact.Members)
            list.Add(new ContactProperty(v3 ? "X-ADDRESSBOOKSERVER-MEMBER" : "MEMBER", member));
        list.AddRange(contact.AdditionalProperties);
        return list;
    }

    private static void AddTyped(
        List<ContactProperty> list, Groups groups, string name, string value, ContactTypes types, bool preferred, string? label, bool v3) {
        var parameters = new List<ContactParameter>(2);
        var typeValues = new List<string>();
        foreach (var (flag, typeName) in TypeNames)
            if ((types & flag) != 0) typeValues.Add(v3 ? typeName : typeName.ToLowerInvariant());
        if (preferred && v3) typeValues.Add("pref");
        if (typeValues.Count > 0) parameters.Add(new ContactParameter("TYPE", typeValues));
        if (preferred && !v3) parameters.Add(new ContactParameter("PREF", "1"));

        var group = string.IsNullOrEmpty(label) ? null : groups.Next();
        list.Add(new ContactProperty(group, name, parameters, value));
        if (group is not null) list.Add(new ContactProperty(group, "X-ABLabel", [], ContentText.Escape(label!)));
    }

    private static ContactProperty Date(string? group, string name, ContactDate date, bool v3) {
        var c = CultureInfo.InvariantCulture;
        if (v3) {
            return date.Year is { } year
                ? new ContactProperty(group, name, [], string.Create(c, $"{year:D4}-{date.Month:D2}-{date.Day:D2}"))
                : new ContactProperty(group, name, [new ContactParameter("X-APPLE-OMIT-YEAR", "1604")], string.Create(c, $"1604-{date.Month:D2}-{date.Day:D2}"));
        }

        return date.Year is { } y
            ? new ContactProperty(group, name, [], string.Create(c, $"{y:D4}{date.Month:D2}{date.Day:D2}"))
            : new ContactProperty(group, name, [], string.Create(c, $"--{date.Month:D2}{date.Day:D2}"));
    }

    private static ContactProperty Photo(ContactPhoto photo, bool v3) {
        if (photo.Data is { } data && photo.MediaType is { } mediaType) {
            var base64 = Convert.ToBase64String(data.Span);
            if (v3) {
                var subtype = mediaType[(mediaType.IndexOf('/') + 1)..].ToUpperInvariant();
                return new ContactProperty(null, "PHOTO", [new ContactParameter("ENCODING", "b"), new ContactParameter("TYPE", subtype)], base64);
            }

            return new ContactProperty("PHOTO", $"data:{mediaType};base64,{base64}");
        }

        if (photo.Uri is { } uri)
            return v3 ? new ContactProperty(null, "PHOTO", [new ContactParameter("VALUE", "uri")], uri.AbsoluteUri) : new ContactProperty("PHOTO", uri.AbsoluteUri);

        throw new ContactWriteException("The photo has neither bytes nor an address.", null, "PHOTO");
    }

    private static string Structured(params string?[] parts) {
        var builder = new StringBuilder();
        for (var i = 0; i < parts.Length; i++) {
            if (i > 0) builder.Append(';');
            if (parts[i] is { } part) builder.Append(ContentText.Escape(part));
        }

        return builder.ToString();
    }

    private static string List(IReadOnlyList<string> values) => string.Join(',', values.Select(ContentText.Escape));

    /// <summary>Hands out <c>item1</c>, <c>item2</c>, … skipping the groups the additional properties already use.</summary>
    private sealed class Groups(IReadOnlyList<ContactProperty> additional) {
        private HashSet<string>? _taken;
        private int _next = 1;

        public string Next() {
            _taken ??= new HashSet<string>(
                additional.Where(static property => property.Group is not null).Select(static property => property.Group!),
                StringComparer.OrdinalIgnoreCase);
            while (true) {
                var group = "item" + _next.ToString(CultureInfo.InvariantCulture);
                _next++;
                if (!_taken.Contains(group)) return group;
            }
        }
    }
}
