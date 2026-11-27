using Elarion.Calendars;

namespace Elarion.CalDav;

/// <summary>
/// The CalDAV settings (ADR-0111), bound by <c>AddElarionCalDav(configuration)</c> from the <see cref="SectionName"/>
/// section. The limits of calendar data are <see cref="CalendarRead"/>'s, the format package's own, not names of this
/// package (spec 0006 FR-003); the WebDAV limits are <c>WebDavOptions</c>'.
/// </summary>
public sealed class CalDavOptions {
    /// <summary>The conventional configuration section: <c>CalDav</c>.</summary>
    public const string SectionName = "CalDav";

    /// <summary>
    /// How every calendar object a request or a response carries is read: published objects when they are filtered,
    /// expanded or served as a feed, and what the client receives from a server. The defaults of
    /// <see cref="CalendarReadOptions"/>.
    /// </summary>
    public CalendarReadOptions CalendarRead { get; set; } = new();

    /// <summary>
    /// The caps of every expansion: a <c>calendar-query</c>'s time-range test, <c>expand</c>, <c>limit-recurrence-set</c>,
    /// <c>free-busy-query</c> and the time span stored at publishing. A query whose expansion exceeds them answers with
    /// what the caps allowed. <see cref="CalendarExpandOptions.FloatingTimeZone"/> is ignored: floating times are placed in
    /// each calendar's own time zone (spec 0006 FR-022).
    /// </summary>
    public CalendarExpandOptions CalendarExpand { get; set; } = new();

    /// <summary>Whether published calendars are also served as ICS feeds at <c>{prefix}/feeds/{token}.ics</c> (FR-026). Default true.</summary>
    public bool FeedsEnabled { get; set; } = true;

    /// <summary>
    /// The <c>PRODID</c> of the objects published; changing it changes every object's bytes, and so its ETag, once.
    /// Default <see cref="CalendarWriteOptions.DefaultProductId"/>.
    /// </summary>
    public string ProductId { get; set; } = CalendarWriteOptions.DefaultProductId;
}
