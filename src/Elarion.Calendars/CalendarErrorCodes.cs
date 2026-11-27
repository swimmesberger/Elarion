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

    /// <summary>A recurrence examined more than <see cref="CalendarExpandOptions.MaxIterations"/> candidates without an occurrence.</summary>
    public const string RecurrenceLimit = "calendar.recurrence_limit";
}
