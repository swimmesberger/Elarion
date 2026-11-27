using Elarion.ContentLines;

namespace Elarion.Contacts.Reading;

/// <summary>
/// Maps the property list of one card to a <see cref="Contact"/> (FR-020–FR-036, FR-067). Single-valued properties take
/// their first occurrence and leave later ones among the additional properties, so a card read and written again keeps
/// every property; a value that does not read as its kind is dropped with an error.
/// </summary>
internal static class ContactMapper {
    public static Contact? Map(IReadOnlyList<ContactProperty> properties, int maxPhotoBytes, List<ContactError> errors) {
        var consumed = new bool[properties.Count];
        var labels = CollectLabels(properties);
        HashSet<string>? usedGroups = null;

        string? formattedName = null, organization = null, department = null, title = null, role = null, note = null, uid = null;
        ContactName? name = null;
        var kind = ContactKind.Individual;
        bool sawFn = false, sawN = false, sawOrg = false, sawTitle = false, sawRole = false, sawNote = false, sawUid = false,
            sawKind = false, sawBirthday = false, sawPhoto = false;
        List<string>? nicknames = null, categories = null, members = null;
        List<ContactPhone>? phones = null;
        List<ContactEmail>? emails = null;
        List<ContactAddress>? addresses = null;
        List<ContactUrl>? urls = null;
        ContactDate? birthday = null;
        ContactPhoto? photo = null;
        var anniversaryRank = int.MaxValue;
        ContactDate? anniversary = null;

        for (var i = 0; i < properties.Count; i++) {
            var property = properties[i];
            switch (property.Name.ToUpperInvariant()) {
                case "BEGIN" or "END" or "VERSION":
                    consumed[i] = true;
                    break;
                case "FN" when !sawFn:
                    sawFn = consumed[i] = true;
                    formattedName = NullIfEmpty(ContentText.Unescape(property.Value));
                    break;
                case "N" when !sawN:
                    sawN = consumed[i] = true;
                    name = ReadName(property.Value);
                    break;
                case "NICKNAME":
                    consumed[i] = true;
                    (nicknames ??= []).AddRange(SplitList(property.Value));
                    break;
                case "KIND" when !sawKind:
                    var kindValue = ContentText.Unescape(property.Value).Trim();
                    if (kindValue.Equals("org", StringComparison.OrdinalIgnoreCase) || kindValue.Equals("organization", StringComparison.OrdinalIgnoreCase)) {
                        sawKind = consumed[i] = true;
                        kind = ContactKind.Organization;
                    }
                    else if (kindValue.Equals("individual", StringComparison.OrdinalIgnoreCase)) {
                        sawKind = consumed[i] = true;
                    }
                    else if (kindValue.Equals("group", StringComparison.OrdinalIgnoreCase)) {
                        sawKind = consumed[i] = true;
                        kind = ContactKind.Group;
                    }

                    break;
                // Apple's 3.0 group card (ADR-0114); some servers keep it in 4.0 cards too.
                case "X-ADDRESSBOOKSERVER-KIND" when ContentText.Unescape(property.Value).Trim().Equals("group", StringComparison.OrdinalIgnoreCase):
                    consumed[i] = true;
                    kind = ContactKind.Group;
                    break;
                case "MEMBER":
                case "X-ADDRESSBOOKSERVER-MEMBER":
                    if (property.Value.Trim().Length == 0) break;
                    consumed[i] = true;
                    // A URI value: no text escaping applies, so it is kept as written.
                    (members ??= []).Add(property.Value.Trim());
                    break;
                case "X-ABSHOWAS" when ContentText.Unescape(property.Value).Trim().Equals("COMPANY", StringComparison.OrdinalIgnoreCase):
                    consumed[i] = true;
                    kind = ContactKind.Organization;
                    break;
                case "ORG" when !sawOrg:
                    sawOrg = consumed[i] = true;
                    var units = ContentText.SplitUnescape(property.Value, ';');
                    organization = NullIfEmpty(units[0]);
                    department = units.Count > 1 ? NullIfEmpty(units[1]) : null;
                    break;
                case "TITLE" when !sawTitle:
                    sawTitle = consumed[i] = true;
                    title = NullIfEmpty(ContentText.Unescape(property.Value));
                    break;
                case "ROLE" when !sawRole:
                    sawRole = consumed[i] = true;
                    role = NullIfEmpty(ContentText.Unescape(property.Value));
                    break;
                case "TEL": {
                    consumed[i] = true;
                    var number = ContactValueRules.CleanPhoneNumber(ContentText.Unescape(property.Value)).Trim();
                    if (number.Length == 0) break;
                    var (types, preferred, label) = Typed(properties, property, labels, ref usedGroups);
                    (phones ??= []).Add(new ContactPhone(number) { Types = types, Preferred = preferred, Label = label });
                    break;
                }
                case "EMAIL": {
                    consumed[i] = true;
                    var address = ContentText.Unescape(property.Value).Trim();
                    if (address.Length == 0) break;
                    var (types, preferred, label) = Typed(properties, property, labels, ref usedGroups);
                    (emails ??= []).Add(new ContactEmail(address) { Types = types, Preferred = preferred, Label = label });
                    break;
                }
                case "URL": {
                    consumed[i] = true;
                    var url = ContentText.Unescape(property.Value).Trim();
                    if (url.Length == 0) break;
                    var (types, preferred, label) = Typed(properties, property, labels, ref usedGroups);
                    (urls ??= []).Add(new ContactUrl(url) { Types = types, Preferred = preferred, Label = label });
                    break;
                }
                case "ADR": {
                    consumed[i] = true;
                    var parts = ReadComponents(property.Value, 7);
                    if (parts.All(static part => part is null)) break;
                    var (types, preferred, label) = Typed(properties, property, labels, ref usedGroups);
                    (addresses ??= []).Add(new ContactAddress {
                        PostOfficeBox = parts[0], Extended = parts[1], Street = parts[2], Locality = parts[3],
                        Region = parts[4], PostalCode = parts[5], Country = parts[6],
                        Types = types, Preferred = preferred, Label = label,
                    });
                    break;
                }
                case "BDAY" when !sawBirthday:
                    sawBirthday = consumed[i] = true;
                    if (ContactValueRules.TryReadDate(property, out var bday)) birthday = bday;
                    else errors.Add(InvalidDate(property));
                    break;
                case "ANNIVERSARY" or "X-ANNIVERSARY" or "X-MS-ANNIVERSARY":
                    consumed[i] = true;
                    ReadAnniversary(property, AnniversaryRank(property.Name), ref anniversary, ref anniversaryRank, errors);
                    break;
                case "X-ABDATE" when property.Group is { } dateGroup && labels.TryGetValue(dateGroup, out var dateLabel)
                                     && string.Equals(properties[dateLabel].Value.Trim(), ContactValueRules.AnniversaryLabel, StringComparison.OrdinalIgnoreCase):
                    consumed[i] = true;
                    (usedGroups ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(dateGroup);
                    ReadAnniversary(property, 3, ref anniversary, ref anniversaryRank, errors);
                    break;
                case "NOTE" when !sawNote:
                    sawNote = consumed[i] = true;
                    note = NullIfEmpty(ContentText.Unescape(property.Value));
                    break;
                case "CATEGORIES":
                    consumed[i] = true;
                    (categories ??= []).AddRange(SplitList(property.Value));
                    break;
                case "PHOTO" when !sawPhoto:
                    sawPhoto = consumed[i] = true;
                    photo = PhotoReader.Read(property, maxPhotoBytes, errors);
                    break;
                case "UID" when !sawUid:
                    sawUid = consumed[i] = true;
                    uid = NullIfEmpty(ContentText.Unescape(property.Value));
                    break;
            }
        }

        // A label belongs to the value of its group; a label of a property the model does not map stays with it.
        if (usedGroups is not null) {
            foreach (var labelIndex in labels.Values)
                if (usedGroups.Contains(properties[labelIndex].Group!)) consumed[labelIndex] = true;
        }

        if (formattedName is null && name is null && organization is null && phones is null && emails is null) {
            errors.Add(new ContactError(
                ContactErrorCodes.Empty, "The vCard has no formatted name, name, organization, e-mail address or phone number."));
            return null;
        }

        List<ContactProperty>? additional = null;
        for (var i = 0; i < properties.Count; i++)
            if (!consumed[i]) (additional ??= []).Add(Normalize(properties[i]));

        return new Contact {
            FormattedName = formattedName,
            Name = name,
            Nicknames = nicknames ?? [],
            Kind = kind,
            Organization = organization,
            Department = department,
            Title = title,
            Role = role,
            Phones = phones ?? [],
            Emails = emails ?? [],
            Addresses = addresses ?? [],
            Urls = urls ?? [],
            Birthday = birthday,
            Anniversary = anniversary,
            Note = note,
            Categories = categories ?? [],
            Photo = photo,
            Uid = uid,
            Members = members ?? [],
            AdditionalProperties = additional ?? [],
        };
    }

    /// <summary>
    /// The property without the parameters that described its bytes (<c>CHARSET</c>, a quoted-printable or 8-bit
    /// <c>ENCODING</c>): its value is already decoded, so writing them again would make a reader decode it twice.
    /// </summary>
    public static ContactProperty Normalize(ContactProperty property) {
        if (!property.Parameters.Any(IsTransferParameter)) return property;
        var parameters = new List<ContactParameter>(property.Parameters.Count);
        foreach (var parameter in property.Parameters) {
            if (string.Equals(parameter.Name, "CHARSET", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(parameter.Name, "ENCODING", StringComparison.OrdinalIgnoreCase)) {
                if (parameter.Values.Any(static value => value.Equals("BASE64", StringComparison.OrdinalIgnoreCase) || value.Equals("B", StringComparison.OrdinalIgnoreCase)))
                    parameters.Add(new ContactParameter("ENCODING", "b"));
                continue;
            }

            parameters.Add(parameter);
        }

        return property with { Parameters = parameters };
    }

    private static bool IsTransferParameter(ContactParameter parameter) =>
        string.Equals(parameter.Name, "CHARSET", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(parameter.Name, "ENCODING", StringComparison.OrdinalIgnoreCase) && !(parameter.Values is [var only] && only == "b"));

    private static Dictionary<string, int> CollectLabels(IReadOnlyList<ContactProperty> properties) {
        var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < properties.Count; i++) {
            var property = properties[i];
            if (property.Group is { } group && string.Equals(property.Name, "X-ABLABEL", StringComparison.OrdinalIgnoreCase))
                labels.TryAdd(group, i);
        }

        return labels;
    }

    private static (ContactTypes Types, bool Preferred, string? Label) Typed(
        IReadOnlyList<ContactProperty> properties, ContactProperty property, Dictionary<string, int> labels, ref HashSet<string>? usedGroups) {
        var (types, preferred) = ContactValueRules.ReadTypes(property);
        if (property.Group is not { } group || !labels.TryGetValue(group, out var labelIndex)) return (types, preferred, null);

        (usedGroups ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(group);
        var label = ContentText.Unescape(properties[labelIndex].Value);
        if (ContactValueRules.AppleLabelTypes(label) is { } appleTypes) return (types | appleTypes, preferred, null);

        var custom = ContactValueRules.CustomLabel(label);
        return (types, preferred, custom.Length == 0 ? null : custom);
    }

    private static int AnniversaryRank(string name) => name.ToUpperInvariant() switch {
        "ANNIVERSARY" => 0,
        "X-ANNIVERSARY" => 1,
        _ => 2,
    };

    private static void ReadAnniversary(ContactProperty property, int rank, ref ContactDate? anniversary, ref int bestRank, List<ContactError> errors) {
        if (!ContactValueRules.TryReadDate(property, out var date)) {
            errors.Add(InvalidDate(property));
            return;
        }

        if (rank >= bestRank) return;
        bestRank = rank;
        anniversary = date;
    }

    private static ContactName? ReadName(string value) {
        var parts = ReadComponents(value, 5);
        return parts.All(static part => part is null) ? null : new ContactName(parts[0], parts[1], parts[2], parts[3], parts[4]);
    }

    // Splits a structured value at unescaped ';' into exactly `count` components, ignoring further ones (FR-026); several
    // values of one component, split at unescaped ',', are joined with ", ".
    private static string?[] ReadComponents(string value, int count) {
        var raw = ContentText.SplitRaw(value, ';');
        var parts = new string?[count];
        for (var i = 0; i < count && i < raw.Count; i++) {
            var values = ContentText.SplitUnescape(raw[i], ',');
            var joined = values.Count == 1 ? values[0] : string.Join(", ", values.Where(static v => v.Length > 0));
            parts[i] = NullIfEmpty(joined);
        }

        return parts;
    }

    private static IEnumerable<string> SplitList(string value) =>
        ContentText.SplitUnescape(value, ',').Where(static item => !string.IsNullOrWhiteSpace(item));

    private static ContactError InvalidDate(ContactProperty property) =>
        new(ContactErrorCodes.InvalidValue, $"The value of {property.Name} is not a date.",
            lineNumber: property.LineNumber, property: property.Name, value: property.Value, expectedKind: "date");

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
