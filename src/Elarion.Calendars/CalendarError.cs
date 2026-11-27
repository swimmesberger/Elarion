using System.Globalization;
using System.Text;

namespace Elarion.Calendars;

/// <summary>
/// One problem found while reading or expanding a calendar (spec 0004 FR-020, FR-022): a stable <see cref="Code"/>, the
/// data its wording needs, an English default <see cref="Message"/>, and where it is — the item, its line, the
/// property, its line and its value as written.
/// </summary>
/// <remarks>
/// The location members <see cref="ItemIndex"/> to <see cref="Limit"/> are the ones <c>ContactError</c> of
/// <c>Elarion.Contacts</c> carries too. The constructor is public so a handler's tests can build a result by hand.
/// <see cref="Message"/> does not repeat the location; <see cref="ToString"/> prefixes it.
/// </remarks>
public sealed class CalendarError {
    /// <summary>Creates an error.</summary>
    /// <param name="code">The stable code, one of <see cref="CalendarErrorCodes"/> or the application's own.</param>
    /// <param name="message">The English default message, without the location.</param>
    public CalendarError(string code, string message) {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentNullException.ThrowIfNull(message);
        Code = code;
        Message = message;
    }

    /// <summary>The stable code.</summary>
    public string Code { get; }

    /// <summary>The English default message, without the location.</summary>
    public string Message { get; }

    /// <summary>The 1-based index of the item among the calendar's events and to-dos, or <see langword="null"/> for a file error.</summary>
    public int? ItemIndex { get; init; }

    /// <summary>The line the item starts on.</summary>
    public int? ItemLineNumber { get; init; }

    /// <summary>The line of the property or line the error is about.</summary>
    public int? LineNumber { get; init; }

    /// <summary>The property the error is about, such as <c>DTSTART</c>.</summary>
    public string? Property { get; init; }

    /// <summary>The property's value as written.</summary>
    public string? Value { get; init; }

    /// <summary>The kind of value expected, for <see cref="CalendarErrorCodes.InvalidValue"/>: <c>date-time</c>, <c>integer</c>, <c>duration</c>, …</summary>
    public string? ExpectedKind { get; init; }

    /// <summary>The exceeded limit, for the limit codes.</summary>
    public long? Limit { get; init; }

    /// <summary>The component of the item, such as <c>VEVENT</c>.</summary>
    public string? Component { get; init; }

    /// <summary>The item's <c>UID</c>, when it has one.</summary>
    public string? Uid { get; init; }

    /// <summary>The time-zone id, for <see cref="CalendarErrorCodes.UnknownTimeZone"/>.</summary>
    public string? TimeZoneId { get; init; }

    /// <summary>The message prefixed with its location, for example <c>Item 3 (VEVENT, UID a@b), line 12, DTSTART: …</c>.</summary>
    public override string ToString() {
        var location = new StringBuilder();
        if (ItemIndex is { } index) {
            location.Append("Item ").Append(index.ToString(CultureInfo.InvariantCulture));
            if (Component is not null || Uid is not null) {
                location.Append(" (").Append(Component ?? "item");
                if (Uid is not null) location.Append(", UID ").Append(Uid);
                location.Append(')');
            }
        }

        if ((LineNumber ?? ItemLineNumber) is { } line) {
            if (location.Length > 0) location.Append(", ");
            location.Append("line ").Append(line.ToString(CultureInfo.InvariantCulture));
        }

        if (Property is not null) {
            if (location.Length > 0) location.Append(", ");
            location.Append(Property);
        }

        return location.Length == 0 ? Message : location.Append(": ").Append(Message).ToString();
    }

    internal CalendarError Locate(int index, int itemLine, string component, string? uid) => new(Code, Message) {
        ItemIndex = index,
        ItemLineNumber = itemLine,
        LineNumber = LineNumber,
        Property = Property,
        Value = Value,
        ExpectedKind = ExpectedKind,
        Limit = Limit,
        Component = component,
        Uid = uid,
        TimeZoneId = TimeZoneId,
    };
}
