using Elarion.Contacts;
using Elarion.WebDav;

namespace Elarion.CardDav;

/// <summary>How a contact becomes a stored object: its name, its bytes and its content type.</summary>
internal static class CardDavObjects {
    /// <summary>The media type of contact data as stored and served.</summary>
    public const string ContentType = "text/vcard; charset=utf-8";

    /// <summary>The stable member name of <paramref name="uid"/>, with <c>.vcf</c>.</summary>
    public static string NameOf(string uid) => DavObjectNames.NameOf(uid, ".vcf");

    /// <summary>
    /// Writes <paramref name="contact"/> as one vCard 3.0 card (spec 0007 FR-009) and describes it for the store. The
    /// writer writes no clock value, so equal contacts give equal bytes and an equal ETag.
    /// </summary>
    /// <exception cref="ArgumentException">The contact has no UID, or nothing to show as its <c>FN</c> (FR-010, FR-031).</exception>
    /// <exception cref="ContactWriteException">The writer cannot represent the contact.</exception>
    public static async ValueTask<DavObjectContent> ToContentAsync(Contact contact, ContactReadOptions readOptions, CancellationToken cancellationToken) {
        if (string.IsNullOrEmpty(contact.Uid))
            throw new ArgumentException("A published contact needs a UID: it names the contact's URL and keeps it stable.", nameof(contact));
        // The 3.0 writer writes FN from the display name; an empty FN makes Apple drop the card silently.
        if (string.IsNullOrWhiteSpace(contact.DisplayName))
            throw new ArgumentException(
                $"The contact '{contact.Uid}' has nothing to show as its name (no formatted name, name, organization, e-mail or phone); "
                + "contacts apps would drop it.", nameof(contact));

        // Every stored card must read back to be converted or queried (FR-034); a large group is the usual way past the limit.
        var count = contact.ToProperties(ContactFormatVersion.V3_0).Count + 1;
        if (count > readOptions.MaxPropertiesPerContact)
            throw new ArgumentException(
                $"The contact '{contact.Uid}' has {count} properties, more than ContactReadOptions.MaxPropertiesPerContact "
                + $"({readOptions.MaxPropertiesPerContact}); raise CardDav:ContactRead:MaxPropertiesPerContact for larger groups.", nameof(contact));

        var bytes = await WriteAsync(contact, ContactFormatVersion.V3_0, cancellationToken).ConfigureAwait(false);
        return new DavObjectContent {
            Name = NameOf(contact.Uid),
            Uid = contact.Uid,
            Content = bytes,
            ContentType = ContentType,
            ETag = DavETag.Compute(bytes),
        };
    }

    /// <summary>Writes one contact as one card of <paramref name="version"/>.</summary>
    public static async ValueTask<byte[]> WriteAsync(Contact contact, ContactFormatVersion version, CancellationToken cancellationToken) {
        using var stream = new MemoryStream();
        var writer = ContactWriter.Create(stream, new ContactWriteOptions { Version = version });
        await using (writer.ConfigureAwait(false)) await writer.AddAsync(contact, cancellationToken).ConfigureAwait(false);
        return stream.ToArray();
    }

    /// <summary>Writes one property list as one card of <paramref name="version"/>.</summary>
    public static async ValueTask<byte[]> WriteAsync(IReadOnlyList<ContactProperty> card, ContactFormatVersion version, CancellationToken cancellationToken) {
        using var stream = new MemoryStream();
        var writer = ContactWriter.Create(stream, new ContactWriteOptions { Version = version });
        await using (writer.ConfigureAwait(false)) await writer.AddAsync(card, cancellationToken).ConfigureAwait(false);
        return stream.ToArray();
    }
}
