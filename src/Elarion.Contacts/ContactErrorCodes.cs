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
    /// <summary>
    /// The input holds no vCard: no <c>BEGIN:VCARD</c> line (a PDF, an empty file, a spreadsheet).
    /// <see cref="ContactError.MediaType"/> says what the bytes are when they are not text, and is <see langword="null"/>
    /// for a text without a vCard.
    /// </summary>
    public const string Unreadable = "contact.unreadable";

    /// <summary>The input is larger than <see cref="ContactReadOptions.MaxBytes"/>; see <see cref="ContactError.Limit"/>.</summary>
    public const string TooLarge = "contact.too_large";

    /// <summary>The input holds more vCards than <see cref="ContactReadOptions.MaxContacts"/>; the entries before the limit are kept.</summary>
    public const string TooManyContacts = "contact.too_many_contacts";

    /// <summary>
    /// A vCard has more lines than <see cref="ContactReadOptions.MaxPropertiesPerContact"/>; its entry has no contact and
    /// keeps only the properties that name it.
    /// </summary>
    public const string TooManyProperties = "contact.too_many_properties";

    /// <summary>
    /// The input holds more lines in its vCards than <see cref="ContactReadOptions.MaxProperties"/>; the entries before
    /// the card being read are kept.
    /// </summary>
    public const string TooManyPropertiesInFile = "contact.too_many_properties_in_file";

    /// <summary>
    /// The input has more problems than <see cref="ContactReadOptions.MaxErrors"/>; the rest are counted in
    /// <see cref="ContactReadResult.OmittedErrorCount"/>, not listed. The read is complete.
    /// </summary>
    public const string TooManyErrors = "contact.too_many_errors";

    /// <summary>
    /// A line has more parameter values than <see cref="ContactReadOptions.MaxParametersPerProperty"/>; the property is
    /// dropped and the card is read on.
    /// </summary>
    public const string TooManyParameters = "contact.too_many_parameters";

    /// <summary>An unfolded line is longer than <see cref="ContactReadOptions.MaxLineBytes"/>; the property is dropped.</summary>
    public const string LineTooLong = "contact.line_too_long";

    /// <summary>A photo is larger than <see cref="ContactReadOptions.MaxPhotoBytes"/>; the photo is dropped.</summary>
    public const string PhotoTooLarge = "contact.photo_too_large";

    /// <summary>A value does not read as its kind; see <see cref="ContactError.ExpectedKind"/>. The property is dropped.</summary>
    public const string InvalidValue = "contact.invalid_value";

    /// <summary>
    /// A birthday or an anniversary that names no full date, such as <c>1985</c>, <c>1985-04</c>, <c>--04</c> or
    /// <c>---12</c> (vCard 4.0 allows them); <see cref="ContactDate"/> needs a month and a day, so the value is dropped.
    /// </summary>
    public const string PartialDate = "contact.partial_date";

    /// <summary>A <c>CHARSET</c> the runtime does not know; see <see cref="ContactError.Charset"/>. The value is decoded by the ladder.</summary>
    public const string UnknownCharset = "contact.unknown_charset";

    /// <summary>A line inside a vCard without a name or a colon; it is skipped.</summary>
    public const string MalformedLine = "contact.malformed_line";

    /// <summary>A vCard without a formatted name, name, organization, e-mail address or phone number; its entry has no contact.</summary>
    public const string Empty = "contact.empty";

    /// <summary>A vCard not closed by <c>END:VCARD</c> before the next <c>BEGIN:VCARD</c> or the end of the input; it is read.</summary>
    public const string UnexpectedEnd = "contact.unexpected_end";

    /// <summary>
    /// Whether <paramref name="code"/> is about the file's form rather than a contact's values (FR-091): the file is not
    /// a vCard file or past a file limit, a card past a per-card limit or not closed, a line malformed, too long or with
    /// too many parameters. An application that refuses broken files refuses when one of these occurs; one that imports
    /// what it can checks only <see cref="ContactReadResult.FileErrors"/> for them, since a structural file error means
    /// the read stopped or never started. <see cref="TooManyErrors"/> is not structural: the read is complete and only
    /// the listing of errors stopped. The value errors (<see cref="InvalidValue"/>, <see cref="PartialDate"/>,
    /// <see cref="PhotoTooLarge"/>, <see cref="UnknownCharset"/>, <see cref="Empty"/>) and an application's own codes are
    /// not structural either.
    /// </summary>
    /// <example>
    /// <code>
    /// // The file is not a vCard file, or the read stopped early: refuse the upload.
    /// if (result.FileErrors.Any(static e => ContactErrorCodes.IsStructural(e.Code))) return result.ToAppError("contacts");
    /// </code>
    /// </example>
    public static bool IsStructural(string code) => code is Unreadable or TooLarge or TooManyContacts or TooManyPropertiesInFile
        or TooManyProperties or TooManyParameters or LineTooLong or MalformedLine or UnexpectedEnd;
}
