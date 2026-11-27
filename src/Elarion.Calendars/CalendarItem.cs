using Elarion.Calendars.Reading;

namespace Elarion.Calendars;

/// <summary>The <c>CLASS</c> of an item.</summary>
public enum CalendarClassification {
    /// <summary><c>PUBLIC</c>.</summary>
    Public,

    /// <summary><c>PRIVATE</c>.</summary>
    Private,

    /// <summary><c>CONFIDENTIAL</c>.</summary>
    Confidential,
}

/// <summary>The <c>TRANSP</c> of an event: whether it blocks time.</summary>
public enum CalendarTransparency {
    /// <summary><c>OPAQUE</c>: the event blocks time.</summary>
    Opaque,

    /// <summary><c>TRANSPARENT</c>: the event does not block time.</summary>
    Transparent,
}

/// <summary>The component kinds a calendar holds, as CalDAV's <c>supported-calendar-component-set</c> lists them.</summary>
[Flags]
public enum CalendarComponentKinds {
    /// <summary>None.</summary>
    None = 0,

    /// <summary><c>VEVENT</c>.</summary>
    Event = 1,

    /// <summary><c>VTODO</c>.</summary>
    Todo = 2,

    /// <summary><c>VJOURNAL</c>.</summary>
    Journal = 4,

    /// <summary><c>VFREEBUSY</c>.</summary>
    FreeBusy = 8,

    /// <summary><c>VAVAILABILITY</c>.</summary>
    Availability = 16,
}

/// <summary>The <c>ORGANIZER</c> of an item.</summary>
public sealed class CalendarOrganizer {
    /// <summary>The calendar user address, usually a <c>mailto:</c> URI.</summary>
    public required string Address { get; set; }

    /// <summary>The display name, <c>CN</c>.</summary>
    public string? CommonName { get; set; }

    /// <summary>The parameters the model does not map, kept for writing.</summary>
    public IList<CalendarParameter> AdditionalParameters { get; set; } = new List<CalendarParameter>();
}

/// <summary>An <c>ATTENDEE</c> of an item.</summary>
public sealed class CalendarAttendee {
    /// <summary>The calendar user address, usually a <c>mailto:</c> URI.</summary>
    public required string Address { get; set; }

    /// <summary>The display name, <c>CN</c>.</summary>
    public string? CommonName { get; set; }

    /// <summary>The role, <c>ROLE</c>: <c>REQ-PARTICIPANT</c>, <c>OPT-PARTICIPANT</c>, <c>CHAIR</c>, …</summary>
    public string? Role { get; set; }

    /// <summary>The participation status, <c>PARTSTAT</c>: <c>NEEDS-ACTION</c>, <c>ACCEPTED</c>, <c>DECLINED</c>, …</summary>
    public string? ParticipationStatus { get; set; }

    /// <summary>Whether a reply is expected, <c>RSVP</c>.</summary>
    public bool? Rsvp { get; set; }

    /// <summary>The kind of calendar user, <c>CUTYPE</c>: <c>INDIVIDUAL</c>, <c>GROUP</c>, <c>RESOURCE</c>, <c>ROOM</c>, …</summary>
    public string? UserType { get; set; }

    /// <summary>The parameters the model does not map, kept for writing.</summary>
    public IList<CalendarParameter> AdditionalParameters { get; set; } = new List<CalendarParameter>();
}

/// <summary>A <c>CONFERENCE</c> of an item (RFC 7986): a URI to join it.</summary>
public sealed class CalendarConference {
    /// <summary>The URI to join, such as <c>https://…</c> or <c>tel:…</c>.</summary>
    public required string Uri { get; set; }

    /// <summary>The features, <c>FEATURE</c>: <c>AUDIO</c>, <c>VIDEO</c>, <c>CHAT</c>, …</summary>
    public IList<string> Features { get; set; } = new List<string>();

    /// <summary>The label, <c>LABEL</c>.</summary>
    public string? Label { get; set; }
}

/// <summary>An <c>RDATE</c> of <c>VALUE=PERIOD</c>: an extra occurrence with its own end or duration.</summary>
public sealed record CalendarPeriod {
    /// <summary>The start.</summary>
    public required CalendarDateTime Start { get; init; }

    /// <summary>The end, when the period states one.</summary>
    public CalendarDateTime? End { get; init; }

    /// <summary>The duration, when the period states one.</summary>
    public TimeSpan? Duration { get; init; }
}

/// <summary>
/// What a <c>VEVENT</c> and a <c>VTODO</c> share (spec 0004 FR-039): a view over the item's component that reads the
/// properties it models and keeps every other property and sub-component (FR-040), so writing the item writes them
/// unchanged.
/// </summary>
/// <remarks>The model is mutable, so an application can build items in code; it is not thread-safe.</remarks>
public abstract class CalendarItem {
    private protected CalendarItem() { }

    /// <summary>The component name: <c>VEVENT</c> or <c>VTODO</c>.</summary>
    public abstract string ComponentName { get; }

    /// <summary>The unique identifier, <c>UID</c>.</summary>
    public string? Uid { get; set; }

    /// <summary>When the item was written, <c>DTSTAMP</c>.</summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>When the item was created, <c>CREATED</c>.</summary>
    public DateTimeOffset? Created { get; set; }

    /// <summary>When the item was last changed, <c>LAST-MODIFIED</c>.</summary>
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>The revision, <c>SEQUENCE</c>.</summary>
    public int? Sequence { get; set; }

    /// <summary>The start, <c>DTSTART</c>.</summary>
    public CalendarDateTime? Start { get; set; }

    /// <summary>The instance of a series this item overrides, <c>RECURRENCE-ID</c>.</summary>
    public CalendarDateTime? RecurrenceId { get; set; }

    /// <summary>The title, <c>SUMMARY</c>.</summary>
    public string? Summary { get; set; }

    /// <summary>The description, <c>DESCRIPTION</c>.</summary>
    public string? Description { get; set; }

    /// <summary>The location, <c>LOCATION</c>.</summary>
    public string? Location { get; set; }

    /// <summary>The categories, every <c>CATEGORIES</c> value.</summary>
    public IList<string> Categories { get; set; } = new List<string>();

    /// <summary>The classification, <c>CLASS</c>.</summary>
    public CalendarClassification? Class { get; set; }

    /// <summary>The priority, <c>PRIORITY</c>: 0 undefined, 1 highest, 9 lowest.</summary>
    public int? Priority { get; set; }

    /// <summary>A URL of the item, <c>URL</c>.</summary>
    public Uri? Url { get; set; }

    /// <summary>The position, <c>GEO</c>: latitude and longitude.</summary>
    public (double Latitude, double Longitude)? Geo { get; set; }

    /// <summary>The organizer, <c>ORGANIZER</c>.</summary>
    public CalendarOrganizer? Organizer { get; set; }

    /// <summary>The attendees, <c>ATTENDEE</c>.</summary>
    public IList<CalendarAttendee> Attendees { get; set; } = new List<CalendarAttendee>();

    /// <summary>The status, <c>STATUS</c>, as written in upper case: <c>CONFIRMED</c>, <c>CANCELLED</c>, <c>COMPLETED</c>, …</summary>
    public string? Status { get; set; }

    /// <summary>The recurrence rules, <c>RRULE</c>.</summary>
    public IList<CalendarRecurrenceRule> RecurrenceRules { get; set; } = new List<CalendarRecurrenceRule>();

    /// <summary>The extra instances, <c>RDATE</c> of a date or date-time.</summary>
    public IList<CalendarDateTime> RecurrenceDates { get; set; } = new List<CalendarDateTime>();

    /// <summary>The extra instances with their own length, <c>RDATE;VALUE=PERIOD</c>.</summary>
    public IList<CalendarPeriod> RecurrencePeriods { get; set; } = new List<CalendarPeriod>();

    /// <summary>The excluded instances, <c>EXDATE</c>.</summary>
    public IList<CalendarDateTime> ExceptionDates { get; set; } = new List<CalendarDateTime>();

    /// <summary>
    /// The exception rules, <c>EXRULE</c>: RFC 2445 rules whose instances are excluded from the series. RFC 5545
    /// deprecates them, but older producers still write them; expansion applies them, and <c>DTSTART</c> is never excluded.
    /// </summary>
    public IList<CalendarRecurrenceRule> ExceptionRules { get; set; } = new List<CalendarRecurrenceRule>();

    /// <summary>The alarms, <c>VALARM</c>.</summary>
    public IList<CalendarAlarm> Alarms { get; set; } = new List<CalendarAlarm>();

    /// <summary>The color, <c>COLOR</c> (RFC 7986), a CSS color name.</summary>
    public string? Color { get; set; }

    /// <summary>The conferences, <c>CONFERENCE</c> (RFC 7986).</summary>
    public IList<CalendarConference> Conferences { get; set; } = new List<CalendarConference>();

    /// <summary>The comments, <c>COMMENT</c>.</summary>
    public IList<string> Comments { get; set; } = new List<string>();

    /// <summary>The contacts, <c>CONTACT</c>.</summary>
    public IList<string> Contacts { get; set; } = new List<string>();

    /// <summary>The resources, every <c>RESOURCES</c> value.</summary>
    public IList<string> Resources { get; set; } = new List<string>();

    /// <summary>The related items' <c>UID</c>s, <c>RELATED-TO</c>.</summary>
    public IList<string> RelatedTo { get; set; } = new List<string>();

    /// <summary>The properties the model does not map, in file order (FR-040).</summary>
    public IList<CalendarProperty> AdditionalProperties { get; set; } = new List<CalendarProperty>();

    /// <summary>The sub-components other than <c>VALARM</c>, in file order (FR-040).</summary>
    public IList<CalendarComponent> AdditionalComponents { get; set; } = new List<CalendarComponent>();

    /// <summary>Whether the item has a rule or extra dates, so it describes a series.</summary>
    public bool IsRecurring => RecurrenceRules.Count > 0 || RecurrenceDates.Count > 0 || RecurrencePeriods.Count > 0;

    /// <summary>
    /// The item as a component (FR-042): the mapped properties in a fixed order (FR-046), then the additional ones in
    /// their order, then the alarms, then the additional components.
    /// </summary>
    public CalendarComponent ToComponent() => CalendarItemMapper.ToComponent(this, null);

    /// <summary>The zones the item's date-times use, in order of first use.</summary>
    public IReadOnlyList<TimeZoneInfo> GetTimeZones() => CalendarItemMapper.TimeZonesOf(this);
}

/// <summary>A <c>VEVENT</c> (spec 0004 FR-039).</summary>
/// <example>
/// <code>
/// var shift = new CalendarEvent {
///     Uid = $"shift-{id}@example.com",
///     Timestamp = changedAt,
///     Summary = "Early shift",
///     Start = CalendarDateTime.Zoned(new DateTime(2026, 12, 1, 6, 0, 0), vienna),
///     End = CalendarDateTime.Zoned(new DateTime(2026, 12, 1, 14, 0, 0), vienna),
/// };
/// </code>
/// </example>
public sealed class CalendarEvent : CalendarItem {
    /// <inheritdoc/>
    public override string ComponentName => "VEVENT";

    /// <summary>The end, <c>DTEND</c>, exclusive.</summary>
    public CalendarDateTime? End { get; set; }

    /// <summary>The length, <c>DURATION</c>, when the event states one instead of an end.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Whether the event lasts whole days: its start is a date.</summary>
    public bool IsAllDay => Start is { IsDate: true };

    /// <summary>Whether the event blocks time, <c>TRANSP</c>.</summary>
    public CalendarTransparency? Transparency { get; set; }

    /// <summary>
    /// Reads an event from its component, resolving each <c>TZID</c> through the system's zones (spec 0004 FR-042);
    /// <see langword="false"/> with the errors when a time cannot be read.
    /// </summary>
    public static bool TryFromComponent(CalendarComponent component, out CalendarEvent? value, out IReadOnlyList<CalendarError> errors) {
        var ok = CalendarItemMapper.TryMap(component, null, out var item, out errors);
        value = item as CalendarEvent;
        return ok && value is not null;
    }
}

/// <summary>A <c>VTODO</c> (spec 0004 FR-039).</summary>
public sealed class CalendarTodo : CalendarItem {
    /// <inheritdoc/>
    public override string ComponentName => "VTODO";

    /// <summary>When the to-do is due, <c>DUE</c>.</summary>
    public CalendarDateTime? Due { get; set; }

    /// <summary>The length, <c>DURATION</c>, counted from <see cref="CalendarItem.Start"/>.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>When the to-do was completed, <c>COMPLETED</c>.</summary>
    public DateTimeOffset? Completed { get; set; }

    /// <summary>How far it is done, <c>PERCENT-COMPLETE</c>, 0 to 100.</summary>
    public int? PercentComplete { get; set; }

    /// <summary>
    /// Reads a to-do from its component, resolving each <c>TZID</c> through the system's zones;
    /// <see langword="false"/> with the errors when a time cannot be read.
    /// </summary>
    public static bool TryFromComponent(CalendarComponent component, out CalendarTodo? value, out IReadOnlyList<CalendarError> errors) {
        var ok = CalendarItemMapper.TryMap(component, null, out var item, out errors);
        value = item as CalendarTodo;
        return ok && value is not null;
    }
}
