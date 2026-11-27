namespace Elarion.Contacts;

/// <summary>
/// The stable codes of <see cref="ContactError"/> (FR-041), in the framework's <c>area.snake_case</c> form, so an
/// application words every problem of an import in its own language without parsing a message.
/// </summary>
/// <remarks>
/// The codes belong to a read result, not to an <c>AppError</c> a handler returns, so they live here and not in
/// <c>ErrorCodes</c>. An application's own entry errors (<see cref="ContactEntry.AddError"/>) use codes of its own,
/// such as <c>import.duplicate</c>, never the <c>contact.</c> prefix.
/// </remarks>
public static class ContactErrorCodes {
    /// <summary>The input holds no vCard: no <c>BEGIN:VCARD</c> line (a PDF, an empty file, a spreadsheet).</summary>
    public const string Unreadable = "contact.unreadable";

    /// <summary>The input is larger than <see cref="ContactReadOptions.MaxBytes"/>; see <see cref="ContactError.Limit"/>.</summary>
    public const string TooLarge = "contact.too_large";

    /// <summary>The input holds more vCards than <see cref="ContactReadOptions.MaxContacts"/>; the entries before the limit are kept.</summary>
    public const string TooManyContacts = "contact.too_many_contacts";

    /// <summary>A vCard has more lines than <see cref="ContactReadOptions.MaxPropertiesPerContact"/>; its entry has no contact.</summary>
    public const string TooManyProperties = "contact.too_many_properties";

    /// <summary>An unfolded line is longer than <see cref="ContactReadOptions.MaxLineBytes"/>; the property is dropped.</summary>
    public const string LineTooLong = "contact.line_too_long";

    /// <summary>A photo is larger than <see cref="ContactReadOptions.MaxPhotoBytes"/>; the photo is dropped.</summary>
    public const string PhotoTooLarge = "contact.photo_too_large";

    /// <summary>A value does not read as its kind; see <see cref="ContactError.ExpectedKind"/>. The property is dropped.</summary>
    public const string InvalidValue = "contact.invalid_value";

    /// <summary>A <c>CHARSET</c> the runtime does not know; see <see cref="ContactError.Charset"/>. The value is decoded by the ladder.</summary>
    public const string UnknownCharset = "contact.unknown_charset";

    /// <summary>A line inside a vCard without a name or a colon; it is skipped.</summary>
    public const string MalformedLine = "contact.malformed_line";

    /// <summary>A vCard without a formatted name, name, organization, e-mail address or phone number; its entry has no contact.</summary>
    public const string Empty = "contact.empty";

    /// <summary>A vCard not closed by <c>END:VCARD</c> before the next <c>BEGIN:VCARD</c> or the end of the input; it is read.</summary>
    public const string UnexpectedEnd = "contact.unexpected_end";
}
