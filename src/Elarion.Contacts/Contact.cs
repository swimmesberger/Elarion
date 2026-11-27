using System.Diagnostics.CodeAnalysis;
using Elarion.Contacts.Reading;
using Elarion.Contacts.Writing;

namespace Elarion.Contacts;

/// <summary>
/// A contact (ADR-0110, decision 1): the fixed model of a vCard, a view over its property list that keeps every
/// property it does not map in <see cref="AdditionalProperties"/>. The same model is read and written.
/// </summary>
/// <remarks>
/// <para>
/// Equality is structural: lists compare element by element and photos by their bytes, so a contact written and read
/// back equals the one written (FR-065), except that a contact without a formatted name reads back with the one the
/// writer derived, and a line break reads back as <c>\n</c>.
/// </para>
/// <para>
/// An application whose own entity is also called <c>Contact</c> aliases one of the two:
/// <c>using VContact = Elarion.Contacts.Contact;</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var contact = new Contact {
///     Name = new ContactName("Doe", "Jane", null, null, null),
///     Phones = [new ContactPhone("+43 660 1234567") { Types = ContactTypes.Cell, Preferred = true }],
///     Emails = [new ContactEmail("jane@example.com") { Types = ContactTypes.Work }],
///     Birthday = new ContactDate(null, 3, 22),
/// };
/// </code>
/// </example>
public sealed class Contact : IEquatable<Contact> {
    /// <summary>The formatted name (<c>FN</c>) as the file states it.</summary>
    public string? FormattedName { get; init; }

    /// <summary>The structured name (<c>N</c>).</summary>
    public ContactName? Name { get; init; }

    /// <summary>The nicknames (<c>NICKNAME</c>).</summary>
    public IReadOnlyList<string> Nicknames { get; init; } = [];

    /// <summary>Whether the contact is a person or an organization (FR-025).</summary>
    public ContactKind Kind { get; init; }

    /// <summary>The organization: the first component of <c>ORG</c>.</summary>
    public string? Organization { get; init; }

    /// <summary>The department: the second component of <c>ORG</c>.</summary>
    public string? Department { get; init; }

    /// <summary>The job title (<c>TITLE</c>).</summary>
    public string? Title { get; init; }

    /// <summary>The role (<c>ROLE</c>).</summary>
    public string? Role { get; init; }

    /// <summary>The phone numbers (<c>TEL</c>), in file order.</summary>
    public IReadOnlyList<ContactPhone> Phones { get; init; } = [];

    /// <summary>The e-mail addresses (<c>EMAIL</c>), in file order.</summary>
    public IReadOnlyList<ContactEmail> Emails { get; init; } = [];

    /// <summary>The postal addresses (<c>ADR</c>), in file order.</summary>
    public IReadOnlyList<ContactAddress> Addresses { get; init; } = [];

    /// <summary>The web addresses (<c>URL</c>), in file order.</summary>
    public IReadOnlyList<ContactUrl> Urls { get; init; } = [];

    /// <summary>The birthday (<c>BDAY</c>).</summary>
    public ContactDate? Birthday { get; init; }

    /// <summary>
    /// The anniversary: <c>ANNIVERSARY</c>, <c>X-ANNIVERSARY</c>, <c>X-MS-ANNIVERSARY</c> or an <c>X-ABDATE</c> labelled
    /// <c>_$!&lt;Anniversary&gt;!$_</c>, in that order of precedence (FR-024).
    /// </summary>
    public ContactDate? Anniversary { get; init; }

    /// <summary>
    /// The note: the first <c>NOTE</c>, its line breaks as <c>\n</c>. A further one stays among
    /// <see cref="AdditionalProperties"/>; <see cref="ContactProperty.GetText"/> reads it.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>The categories (<c>CATEGORIES</c>).</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>
    /// The photo: the first <c>PHOTO</c> of the card that reads (FR-032); one that does not costs its error and the next
    /// is tried. A further one stays among <see cref="AdditionalProperties"/>; <see cref="ContactPhoto.TryRead"/> reads it.
    /// </summary>
    public ContactPhoto? Photo { get; init; }

    /// <summary>The unique identifier (<c>UID</c>).</summary>
    public string? Uid { get; init; }

    /// <summary>
    /// The member references of a group (<see cref="ContactKind.Group"/>), in order: URIs such as
    /// <c>urn:uuid:{uid}</c> (<see cref="ContactMember.FromUid"/>) or <c>mailto:</c> addresses (<c>MEMBER</c> in vCard 4.0,
    /// <c>X-ADDRESSBOOKSERVER-MEMBER</c> in 3.0; FR-069). Empty for every other contact.
    /// </summary>
    public IReadOnlyList<string> Members { get; init; } = [];

    /// <summary>
    /// The properties the model does not map (<c>IMPP</c>, <c>X-SOCIALPROFILE</c>, <c>AGENT</c>, a second <c>NOTE</c> or <c>PHOTO</c>,
    /// …), with their group, parameters and still-escaped value, written back as they were read (FR-060). The
    /// <c>CHARSET</c> and quoted-printable <c>ENCODING</c> parameters are left out, because the value is decoded.
    /// </summary>
    public IReadOnlyList<ContactProperty> AdditionalProperties { get; init; } = [];

    /// <summary>
    /// The name to show (FR-027): the formatted name, else the parts of the name, else the organization, else the first
    /// e-mail address, else the first phone number; empty when the contact has none of them.
    /// </summary>
    public string DisplayName {
        get {
            if (!string.IsNullOrWhiteSpace(FormattedName)) return FormattedName;
            var name = Name?.ToString();
            if (!string.IsNullOrWhiteSpace(name)) return name;
            if (!string.IsNullOrWhiteSpace(Organization)) return Organization;
            if (Emails.Count > 0) return Emails[0].Address;
            return Phones.Count > 0 ? Phones[0].Number : "";
        }
    }

    /// <summary>
    /// The contact as the properties a card of <paramref name="version"/> holds, in the order the writer writes them,
    /// without <c>BEGIN</c>, <c>VERSION</c> and <c>END</c> (FR-067).
    /// </summary>
    /// <param name="version">The vCard version whose forms the properties take.</param>
    public IReadOnlyList<ContactProperty> ToProperties(ContactFormatVersion version = ContactFormatVersion.V3_0) =>
        ContactSerializer.ToProperties(this, version);

    /// <summary>
    /// Maps a property list to a contact by the rules a read applies (FR-067): a list read from a file, or built or
    /// pruned in code with still-escaped values.
    /// </summary>
    /// <param name="properties">The properties of one card; <c>BEGIN</c>, <c>VERSION</c> and <c>END</c> are ignored.</param>
    /// <param name="contact">The contact, or <see langword="null"/> when nothing usable is left.</param>
    /// <param name="errors">The problems found, located by property and line; without an item location.</param>
    /// <param name="options">The limits a photo is held to; <see langword="null"/> uses the defaults.</param>
    /// <returns>Whether the properties hold a contact; errors may exist either way.</returns>
    public static bool TryFromProperties(
        IReadOnlyList<ContactProperty> properties,
        [NotNullWhen(true)] out Contact? contact,
        out IReadOnlyList<ContactError> errors,
        ContactReadOptions? options = null) {
        ArgumentNullException.ThrowIfNull(properties);
        var list = new List<ContactError>();
        contact = ContactMapper.Map(properties, options?.MaxPhotoBytes ?? ContactReadOptions.DefaultMaxPhotoBytes, list);
        errors = list;
        return contact is not null;
    }

    /// <inheritdoc/>
    public bool Equals(Contact? other) {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return FormattedName == other.FormattedName
            && Equals(Name, other.Name)
            && Nicknames.SequenceEqual(other.Nicknames)
            && Kind == other.Kind
            && Organization == other.Organization
            && Department == other.Department
            && Title == other.Title
            && Role == other.Role
            && Phones.SequenceEqual(other.Phones)
            && Emails.SequenceEqual(other.Emails)
            && Addresses.SequenceEqual(other.Addresses)
            && Urls.SequenceEqual(other.Urls)
            && Birthday == other.Birthday
            && Anniversary == other.Anniversary
            && Note == other.Note
            && Categories.SequenceEqual(other.Categories)
            && Equals(Photo, other.Photo)
            && Uid == other.Uid
            && Members.SequenceEqual(other.Members)
            && AdditionalProperties.SequenceEqual(other.AdditionalProperties);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Contact);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(FormattedName, Name, Kind, Organization, Phones.Count, Emails.Count, Birthday, Uid);

    /// <summary>The display name.</summary>
    public override string ToString() => DisplayName;
}
