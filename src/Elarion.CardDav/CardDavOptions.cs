using Elarion.Contacts;

namespace Elarion.CardDav;

/// <summary>
/// The CardDAV settings (ADR-0113), bound by <c>AddElarionCardDav(configuration)</c> from the <see cref="SectionName"/>
/// section. The limits of contact data are <see cref="ContactRead"/>'s, the format package's own, not names of this
/// package (spec 0007 FR-003); the WebDAV limits are <c>WebDavOptions</c>'.
/// </summary>
public sealed class CardDavOptions {
    /// <summary>The conventional configuration section: <c>CardDav</c>.</summary>
    public const string SectionName = "CardDav";

    /// <summary>
    /// How a stored card is read back: to convert it to vCard 4.0, to prune it to the properties a client named, and to
    /// match it against an <c>addressbook-query</c>. The defaults of <see cref="ContactReadOptions"/>.
    /// </summary>
    public ContactReadOptions ContactRead { get; set; } = new();
}
