using System.Globalization;
using Elarion.ContentLines;

namespace Elarion.Contacts.Reading;

/// <summary>
/// Reads a <c>PHOTO</c> of a card (FR-028–FR-032, FR-050, FR-076): inline Base64 or a <c>data:</c> URI into bounded
/// bytes whose media type is sniffed, an <c>http</c> or <c>https</c> address into a URI. Nothing is ever requested.
/// </summary>
internal static class PhotoReader {
    public static ContactPhoto? Read(ContactProperty property, int maxBytes, List<ContactError> errors) {
        var encoding = property.GetParameter("ENCODING");
        var value = property.Value.Trim();
        if (encoding.Any(static e => e.Equals("B", StringComparison.OrdinalIgnoreCase) || e.Equals("BASE64", StringComparison.OrdinalIgnoreCase)))
            return FromBase64(property, value, DeclaredType(property, null), maxBytes, errors);

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) {
            var comma = value.IndexOf(',');
            if (comma < 0 || !value.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) {
                errors.Add(Invalid(property, "photo", "The photo's data URI is not Base64."));
                return null;
            }

            var dataType = value.AsSpan(5, comma - 5 - ";base64".Length).ToString();
            return FromBase64(property, value[(comma + 1)..], DeclaredType(property, dataType), maxBytes, errors);
        }

        var text = ContentText.Unescape(value).Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)) {
            if (ContactPhoto.IsWebUri(uri)) return ContactPhoto.FromUri(uri);
            errors.Add(Invalid(property, "uri", $"The photo's address uses the scheme '{uri.Scheme}'; only http and https are read."));
            return null;
        }

        errors.Add(Invalid(property, "photo", "The photo is neither inline Base64 nor an http or https address."));
        return null;
    }

    private static ContactPhoto? FromBase64(ContactProperty property, string text, string? declaredType, int maxBytes, List<ContactError> errors) {
        // Count first, so a photo over the limit is refused without decoding it (FR-050).
        var length = 0;
        foreach (var c in text)
            if (!char.IsWhiteSpace(c)) length++;

        var chars = new char[(length + 3) / 4 * 4];
        var at = 0;
        foreach (var c in text)
            if (!char.IsWhiteSpace(c)) chars[at++] = c;

        // Some producers drop the padding.
        while (at < chars.Length) chars[at++] = '=';
        var padding = chars.Length == 0 ? 0 : chars[^1] == '=' ? chars[^2] == '=' ? 2 : 1 : 0;
        var decodedLength = chars.Length / 4 * 3 - padding;
        if (decodedLength > maxBytes) {
            errors.Add(new ContactError(
                ContactErrorCodes.PhotoTooLarge,
                $"The photo is larger than {maxBytes.ToString(CultureInfo.InvariantCulture)} bytes.",
                lineNumber: property.LineNumber, property: property.Name, limit: maxBytes));
            return null;
        }

        var bytes = new byte[Math.Max(decodedLength, 0)];
        if (decodedLength <= 0 || !Convert.TryFromBase64Chars(chars, bytes, out var written)) {
            errors.Add(Invalid(property, "photo", "The photo is not valid Base64."));
            return null;
        }

        if (ContactPhoto.SniffMediaType(bytes.AsSpan(0, written)) is null) {
            // FR-076: what the bytes are, or what the file said they are, so the application can name the format.
            var mediaType = FileSniffer.MediaType(bytes.AsSpan(0, written)) ?? declaredType;
            var shown = mediaType is null ? "" : $" ({mediaType})";
            errors.Add(new ContactError(
                ContactErrorCodes.InvalidValue, $"The photo{shown} is not a JPEG, PNG, GIF, WebP or BMP image.",
                lineNumber: property.LineNumber, property: property.Name, value: property.Value, expectedKind: "photo-format",
                mediaType: mediaType));
            return null;
        }

        return ContactPhoto.FromData(bytes.AsMemory(0, written));
    }

    // The type a file declared: a data URI's, 4.0's MEDIATYPE, or 2.1 and 3.0's TYPE (JPEG, image/jpeg).
    private static string? DeclaredType(ContactProperty property, string? dataUriType) {
        var declared = !string.IsNullOrWhiteSpace(dataUriType)
            ? dataUriType
            : property.GetParameter("MEDIATYPE").FirstOrDefault() ?? property.GetParameter("TYPE").FirstOrDefault();
        if (string.IsNullOrWhiteSpace(declared)) return null;
        declared = declared.Trim().ToLowerInvariant();
        var parameters = declared.IndexOf(';', StringComparison.Ordinal);
        if (parameters >= 0) declared = declared[..parameters].Trim();
        return declared.Contains('/', StringComparison.Ordinal) ? declared : "image/" + declared;
    }

    private static ContactError Invalid(ContactProperty property, string kind, string message) =>
        new(ContactErrorCodes.InvalidValue, message, lineNumber: property.LineNumber, property: property.Name, value: property.Value, expectedKind: kind);
}
