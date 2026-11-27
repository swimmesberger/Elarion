namespace Elarion.Calendars;

/// <summary>
/// The stable codes of <see cref="CalendarError"/> (spec 0004 FR-021), in the framework's <c>area.snake_case</c> form,
/// so an application words every problem of an import in its own language without parsing a message.
/// </summary>
/// <remarks>
/// The codes belong to a read result, not to an <c>AppError</c> a handler returns, so they live here and not in
/// <c>ErrorCodes</c>. An application's own errors (<see cref="CalendarEntry{T}.AddError"/>) use codes with its own
/// prefix, never <c>calendar.</c>.
/// </remarks>
public static class CalendarErrorCodes {
    /// <summary>The input holds no <c>VCALENDAR</c>: an HTML page, a PDF, an empty or binary file.</summary>
    public const string Unreadable = "calendar.unreadable";

    /// <summary>The input is larger than <see cref="CalendarReadOptions.MaxBytes"/>; see <see cref="CalendarError.Limit"/>.</summary>
    public const string TooLarge = "calendar.too_large";

    /// <summary>An unfolded line is longer than <see cref="CalendarReadOptions.MaxLineBytes"/>; the property is dropped.</summary>
    public const string LineTooLong = "calendar.line_too_long";

    /// <summary>The file has more components than <see cref="CalendarReadOptions.MaxComponents"/>; reading stopped.</summary>
    public const string TooManyComponents = "calendar.too_many_components";

    /// <summary>A component has more properties than <see cref="CalendarReadOptions.MaxPropertiesPerComponent"/>.</summary>
    public const string TooManyProperties = "calendar.too_many_properties";

    /// <summary>Components are nested deeper than <see cref="CalendarReadOptions.MaxDepth"/>.</summary>
    public const string NestingTooDeep = "calendar.nesting_too_deep";

    /// <summary>A line is not a content line; it was skipped.</summary>
    public const string MalformedLine = "calendar.malformed_line";

    /// <summary>The file ends inside a component, or a component is closed by the end of an outer one.</summary>
    public const string UnexpectedEnd = "calendar.unexpected_end";

    /// <summary>A required property is missing, such as the <c>DTSTART</c> of an event.</summary>
    public const string Required = "calendar.required";

    /// <summary>A value does not read as its kind; see <see cref="CalendarError.ExpectedKind"/>.</summary>
    public const string InvalidValue = "calendar.invalid_value";

    /// <summary>A <c>TZID</c> neither the system nor the file defines; see <see cref="CalendarError.TimeZoneId"/>.</summary>
    public const string UnknownTimeZone = "calendar.unknown_time_zone";

    /// <summary>Another item has the same <c>UID</c> and <c>RECURRENCE-ID</c>.</summary>
    public const string DuplicateUid = "calendar.duplicate_uid";

    /// <summary>An expansion produced more occurrences than <see cref="CalendarExpandOptions.MaxOccurrences"/>.</summary>
    public const string TooManyOccurrences = "calendar.too_many_occurrences";

    /// <summary>
    /// A recurrence examined more than <see cref="CalendarExpandOptions.MaxIterations"/> periods without an occurrence, or
    /// built more than <see cref="CalendarExpandOptions.MaxCandidates"/> candidates; its expansion stopped.
    /// </summary>
    public const string RecurrenceLimit = "calendar.recurrence_limit";

    /// <summary>The file has more lines than <see cref="CalendarReadOptions.MaxLines"/>; reading stopped.</summary>
    public const string TooManyLines = "calendar.too_many_lines";

    /// <summary>The file has more events and to-dos than <see cref="CalendarReadOptions.MaxItems"/>; reading stopped.</summary>
    public const string TooManyItems = "calendar.too_many_items";

    /// <summary>
    /// A value holds a control character other than tab; reported only by a <see cref="CalendarReadOptions.Strict"/>
    /// read, which skips the line.
    /// </summary>
    public const string ControlCharacter = "calendar.control_character";

    /// <summary>
    /// Whether <paramref name="code"/> is about the file's form rather than an item's values: the file is not a calendar
    /// or past a file limit, a line is malformed, too long or holds a control character, a component ends unexpectedly
    /// or exceeds a per-item limit. An application that refuses broken files refuses when one of these occurs; the
    /// value errors (<see cref="Required"/>, <see cref="InvalidValue"/>, <see cref="UnknownTimeZone"/>,
    /// <see cref="DuplicateUid"/>) and the expansion's are not structural.
    /// </summary>
    /// <remarks>
    /// A per-item limit (<see cref="TooManyProperties"/>, <see cref="NestingTooDeep"/>, and <see cref="LineTooLong"/> inside
    /// an item) is structural, because the item was not read whole. Where an error is reported says what it affects: in
    /// <see cref="CalendarReadResult.FileErrors"/> the file, in an entry's errors only that item, which has no value when
    /// a limit stopped it. A lenient importer that skips broken items and keeps the rest therefore looks at the entry, not
    /// at this method; <see cref="IsLimit"/> tells a limit apart from a broken file.
    /// </remarks>
    public static bool IsStructural(string code) => code is Unreadable or TooLarge or LineTooLong or TooManyComponents
        or TooManyProperties or NestingTooDeep or MalformedLine or UnexpectedEnd or TooManyLines or TooManyItems or ControlCharacter;

    /// <summary>
    /// Whether <paramref name="code"/> reports a limit of <see cref="CalendarReadOptions"/> or
    /// <see cref="CalendarExpandOptions"/> rather than a mistake in the file: <see cref="TooLarge"/>,
    /// <see cref="LineTooLong"/>, <see cref="TooManyComponents"/>, <see cref="TooManyProperties"/>,
    /// <see cref="NestingTooDeep"/>, <see cref="TooManyLines"/>, <see cref="TooManyItems"/>,
    /// <see cref="TooManyOccurrences"/> and <see cref="RecurrenceLimit"/>. Each carries <see cref="CalendarError.Limit"/>
    /// where it has one; an application words them as "too large or too costly" rather than "broken".
    /// </summary>
    public static bool IsLimit(string code) => code is TooLarge or LineTooLong or TooManyComponents or TooManyProperties
        or NestingTooDeep or TooManyLines or TooManyItems or TooManyOccurrences or RecurrenceLimit;
}
