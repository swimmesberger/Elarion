using System.Globalization;
using System.Text;

namespace Elarion.Contacts;

/// <summary>
/// One problem found while reading contacts (ADR-0110, decision 5): a stable <see cref="Code"/>, the data its wording
/// needs, an English default <see cref="Message"/>, and where it is — the entry's index and the line of its
/// <c>BEGIN</c>, the contact's display name, and for a problem with a property its name, its line and its value.
/// </summary>
/// <remarks>
/// The location members are the ones <c>CalendarError</c> carries too; <see cref="DisplayName"/> and
/// <see cref="Charset"/> and <see cref="MediaType"/> are vCard's own. The constructor is public so a handler's tests can build a result by hand.
/// <see cref="Message"/> does not repeat the location; <see cref="ToString"/> prefixes it.
/// </remarks>
public sealed class ContactError {
    /// <summary>The longest <see cref="Value"/> kept; a longer value is cut and ends with an ellipsis.</summary>
    public const int MaxValueLength = 256;

    /// <summary>Creates an error.</summary>
    /// <param name="code">The stable code, one of <see cref="ContactErrorCodes"/> or the application's own.</param>
    /// <param name="message">The English default message, without the location.</param>
    /// <param name="itemIndex">The 1-based index of the entry, or <see langword="null"/> for a problem with the file.</param>
    /// <param name="itemLineNumber">The line of the entry's <c>BEGIN</c>.</param>
    /// <param name="lineNumber">The line of the property or line concerned.</param>
    /// <param name="property">The property's name, such as <c>BDAY</c>.</param>
    /// <param name="value">The property's value as written; cut to <see cref="MaxValueLength"/> characters.</param>
    /// <param name="expectedKind">The kind of value expected (<c>date</c>, <c>photo</c>, <c>uri</c>), for <see cref="ContactErrorCodes.InvalidValue"/>.</param>
    /// <param name="limit">The exceeded limit, for the <c>too_*</c> codes and <see cref="ContactErrorCodes.PhotoTooLarge"/>.</param>
    /// <param name="displayName">The display name of the entry's contact, when it has one.</param>
    /// <param name="charset">The unknown charset, for <see cref="ContactErrorCodes.UnknownCharset"/>.</param>
    /// <param name="mediaType">What the bytes are, for a photo of no readable format or a file that is not text.</param>
    public ContactError(
        string code,
        string message,
        int? itemIndex = null,
        int? itemLineNumber = null,
        int? lineNumber = null,
        string? property = null,
        string? value = null,
        string? expectedKind = null,
        long? limit = null,
        string? displayName = null,
        string? charset = null,
        string? mediaType = null) {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentNullException.ThrowIfNull(message);
        Code = code;
        Message = message;
        ItemIndex = itemIndex;
        ItemLineNumber = itemLineNumber;
        LineNumber = lineNumber;
        Property = property;
        Value = value is { Length: > MaxValueLength } ? string.Concat(value.AsSpan(0, MaxValueLength - 1), "…") : value;
        ExpectedKind = expectedKind;
        Limit = limit;
        DisplayName = displayName;
        Charset = charset;
        MediaType = mediaType;
    }

    /// <summary>The stable code (FR-040, FR-041).</summary>
    public string Code { get; }

    /// <summary>The English default message, without the location.</summary>
    public string Message { get; }

    /// <summary>The 1-based index of the entry, or <see langword="null"/> for a problem with the file.</summary>
    public int? ItemIndex { get; }

    /// <summary>The line of the entry's <c>BEGIN</c>, or <see langword="null"/>.</summary>
    public int? ItemLineNumber { get; }

    /// <summary>The line of the property or line concerned, or <see langword="null"/>.</summary>
    public int? LineNumber { get; }

    /// <summary>The property's name, such as <c>BDAY</c>, or <see langword="null"/>.</summary>
    public string? Property { get; }

    /// <summary>The property's value as written, cut to <see cref="MaxValueLength"/> characters, or <see langword="null"/>.</summary>
    public string? Value { get; }

    /// <summary>
    /// The kind of value expected, for <see cref="ContactErrorCodes.InvalidValue"/>: <c>date</c>, <c>uri</c>, <c>photo</c>
    /// (neither Base64 nor an address), or <c>photo-format</c> (bytes of no image format the package reads; see
    /// <see cref="MediaType"/>).
    /// </summary>
    public string? ExpectedKind { get; }

    /// <summary>The exceeded limit: bytes, contacts, lines or photo bytes.</summary>
    public long? Limit { get; }

    /// <summary>The display name of the entry's contact, when it has one.</summary>
    public string? DisplayName { get; }

    /// <summary>The unknown charset, for <see cref="ContactErrorCodes.UnknownCharset"/>.</summary>
    public string? Charset { get; }

    /// <summary>
    /// What the bytes are, from their magic number or, failing that, the type the file declared: for a photo of no
    /// readable format (<c>image/heic</c>, <c>application/pdf</c>), and for an <see cref="ContactErrorCodes.Unreadable"/>
    /// file that is not text (<c>application/pdf</c>, <c>application/zip</c>, <c>application/octet-stream</c>);
    /// <see langword="null"/> when unknown, and for a text file without a vCard.
    /// </summary>
    public string? MediaType { get; }

    /// <summary>
    /// The message prefixed with its location: <c>Contact 12 (Jane Doe), line 340: …</c>, without the name when the
    /// entry has no contact, or the bare message for a problem with the file.
    /// </summary>
    public override string ToString() {
        if (ItemIndex is not { } index) return Message;
        var location = new StringBuilder("Contact ").Append(index.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(DisplayName)) location.Append(" (").Append(DisplayName).Append(')');
        if ((LineNumber ?? ItemLineNumber) is { } line) location.Append(", line ").Append(line.ToString(CultureInfo.InvariantCulture));
        return location.Append(": ").Append(Message).ToString();
    }

    internal ContactError At(int itemIndex, int itemLineNumber, string? displayName) =>
        new(Code, Message, itemIndex, itemLineNumber, LineNumber, Property, Value, ExpectedKind, Limit, displayName, Charset, MediaType);
}
