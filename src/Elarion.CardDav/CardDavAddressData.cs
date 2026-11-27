using System.Globalization;
using Elarion.Contacts;
using Elarion.WebDav;

namespace Elarion.CardDav;

/// <summary>What an <c>address-data</c> element or an <c>Accept</c> header asks for: a vCard version, and optionally named properties.</summary>
/// <param name="Version">The version to answer.</param>
/// <param name="Properties">The named properties and whether each is wanted without its value; <see langword="null"/> for all.</param>
internal sealed record CardDavAddressDataRequest(ContactFormatVersion Version, IReadOnlyList<(string Name, bool NoValue)>? Properties);

/// <summary>
/// Renders a stored card as a request asks for it (RFC 6352 §10.4, spec 0007 FR-025–FR-028): the stored vCard 3.0 bytes
/// as they are, the card converted to vCard 4.0 through the <see cref="Contact"/> model, or the card pruned to named
/// properties. The ETag stays the stored one (ADR-0113 section 2).
/// </summary>
internal static class CardDavAddressData {
    /// <summary>The media type of <c>address-data</c>.</summary>
    public const string MediaType = "text/vcard";

    /// <summary>The versions <c>supported-address-data</c> lists.</summary>
    public static IReadOnlyList<string> Versions { get; } = ["3.0", "4.0"];

    /// <summary>The stored card, unchanged.</summary>
    public static CardDavAddressDataRequest Stored { get; } = new(ContactFormatVersion.V3_0, null);

    // A card is a card only with these (RFC 6352 §10.4.2 lets the server add what it needs); Apple drops one without FN.
    private static readonly HashSet<string> AlwaysKept = new(["VERSION", "UID", "FN"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads an <c>address-data</c> element: <see cref="Stored"/> for none or an empty one, <see langword="null"/> for a
    /// content type or version this server does not answer.
    /// </summary>
    public static CardDavAddressDataRequest? Parse(DavXmlElement? requested) {
        if (requested is null) return Stored;
        var contentType = requested.Attribute("content-type");
        if (contentType is not null && !IsVCard(contentType)) return null;
        ContactFormatVersion version;
        switch (requested.Attribute("version")?.Trim()) {
            case null or "3.0":
                version = ContactFormatVersion.V3_0;
                break;
            case "4.0":
                version = ContactFormatVersion.V4_0;
                break;
            default:
                return null;
        }

        if (requested.Child(DavNames.Card.AllProp) is not null) return new(version, null);
        var properties = requested.ChildrenNamed(DavNames.Card.Prop)
            .Select(static p => (Name: p.Attribute("name")?.Trim() ?? "", NoValue: string.Equals(p.Attribute("novalue"), "yes", StringComparison.OrdinalIgnoreCase)))
            .Where(static p => p.Name.Length > 0)
            .ToList();
        return new(version, properties.Count == 0 ? null : properties);
    }

    /// <summary>
    /// The version an <c>Accept</c> header prefers: 4.0 only when a range naming <c>text/vcard; version=4.0</c> ranks above
    /// every range that 3.0 satisfies; 3.0 otherwise, as the RFC's default (spec 0007 FR-028).
    /// </summary>
    public static ContactFormatVersion PreferredVersion(string? accept) {
        if (string.IsNullOrWhiteSpace(accept)) return ContactFormatVersion.V3_0;
        double v3 = 0, v4 = 0;
        foreach (var range in accept.Split(',')) {
            var parts = range.Split(';');
            var type = parts[0].Trim();
            double quality = 1;
            string? version = null;
            foreach (var parameter in parts.Skip(1)) {
                var pair = parameter.Split('=', 2);
                if (pair.Length != 2) continue;
                var name = pair[0].Trim();
                var value = pair[1].Trim().Trim('"');
                if (name.Equals("q", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var q))
                    quality = q;
                else if (name.Equals("version", StringComparison.OrdinalIgnoreCase)) version = value;
            }

            var vcard = type.Equals(MediaType, StringComparison.OrdinalIgnoreCase);
            if (vcard && version == "4.0") v4 = Math.Max(v4, quality);
            else if ((vcard && version is null or "3.0") || type is "*/*" || type.Equals("text/*", StringComparison.OrdinalIgnoreCase))
                v3 = Math.Max(v3, quality);
        }

        return v4 > v3 ? ContactFormatVersion.V4_0 : ContactFormatVersion.V3_0;
    }

    /// <summary>The media type of a card of <paramref name="version"/>.</summary>
    public static string ContentTypeOf(ContactFormatVersion version) =>
        version == ContactFormatVersion.V4_0 ? "text/vcard; charset=utf-8; version=4.0" : CardDavObjects.ContentType;

    /// <summary>The stored card as <paramref name="request"/> asks for it.</summary>
    public static async ValueTask<ReadOnlyMemory<byte>> RenderAsync(ReadOnlyMemory<byte> stored, CardDavAddressDataRequest request, ContactReader reader,
        ContactReadOptions readOptions, CancellationToken cancellationToken) {
        if (request.Version == ContactFormatVersion.V3_0 && request.Properties is null) return stored;

        using var input = new MemoryStream(stored.ToArray(), false);
        var read = await reader.ReadAsync(input, readOptions, cancellationToken).ConfigureAwait(false);
        if (read.Entries.Count == 0) return stored;
        var entry = read.Entries[0];

        // 4.0 goes through the model, which keeps every property it does not map, so nothing 3.0 carried is lost.
        if (request.Properties is null)
            return entry.Value is { } contact ? await CardDavObjects.WriteAsync(contact, request.Version, cancellationToken).ConfigureAwait(false) : stored;

        var source = request.Version == ContactFormatVersion.V4_0 && entry.Value is { } model ? model.ToProperties(ContactFormatVersion.V4_0) : entry.Properties;
        var wanted = request.Properties.ToDictionary(static p => p.Name, static p => p.NoValue, StringComparer.OrdinalIgnoreCase);
        var pruned = new List<ContactProperty>();
        foreach (var property in source) {
            if (AlwaysKept.Contains(property.Name)) pruned.Add(property);
            else if (wanted.TryGetValue(property.Name, out var noValue)) pruned.Add(noValue ? property with { Value = "" } : property);
        }

        return await CardDavObjects.WriteAsync(pruned, request.Version, cancellationToken).ConfigureAwait(false);
    }

    // text/vcard, with or without parameters such as charset; the RFC 6352 default.
    private static bool IsVCard(string contentType) {
        var semicolon = contentType.IndexOf(';');
        var type = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
        return type.Equals(MediaType, StringComparison.OrdinalIgnoreCase);
    }
}
