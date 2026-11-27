using AwesomeAssertions;
using Elarion.AppPasswords;
using Elarion.CalDav;
using Elarion.Calendars;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>
/// The calendar-data a report returns (RFC 4791 §9.6, spec 0006 FR-019, FR-027) — expand, limit-recurrence-set, a partial
/// comp/prop selection — and the feed, by edge cases ported from Apple ccs-caldavtester, sabre/dav and sabre/vobject,
/// Radicale, Xandikos, Cyrus (Cassandane) and python-caldav. Each test names its source cases; where the projects
/// disagree, the test pins Elarion's answer and says so.
/// </summary>
public sealed class CalDavExpandConformanceTests {
    private const string Href = CalDavConformance.Href;
    private const string Ns = CalDavConformance.Ns;

    private static string Expand(string start, string end, string inner = "") =>
        $"<C:calendar-data>{inner}<C:expand start=\"{start}\" end=\"{end}\"/></C:calendar-data>";

    /// <summary>The calendar-data of <paramref name="uid"/> through a calendar-multiget with <paramref name="calendarData"/>, unfolded.</summary>
    private static async Task<string> DataAsync(CalDavTestHarness harness, string uid, string calendarData = "<C:calendar-data/>",
        IDictionary<string, string>? headers = null) {
        var response = await harness.SendAsync("REPORT", CalDavConformance.Path,
            $"<C:calendar-multiget {Ns}><D:prop><D:getetag/>{calendarData}</D:prop><D:href>{Href}{uid}.ics</D:href></C:calendar-multiget>",
            headers: headers);
        response.Status.Should().Be(207, response.Body);
        return Unfold(response.Prop(Href + uid + ".ics", DavNames.C.CalendarData)?.Text ?? throw new InvalidOperationException(response.Body));
    }

    private static string Unfold(string text) => text.Replace("\r\n ", "", StringComparison.Ordinal);

    private static int Count(string text, string what) => text.Split(what).Length - 1;

    /// <summary>
    /// Radicale E01, E04 (event_daily_rrule_overridden); Apple E-11; sabre X01, X02, X07; Xandikos E21; python-caldav QY-25:
    /// expanded instances are in UTC with a RECURRENCE-ID each, keep DURATION, drop rules and VTIMEZONE, and an override
    /// replaces its regular instance under its original RECURRENCE-ID.
    /// </summary>
    [Fact]
    public async Task Expand_ReplacesTheOverriddenInstance_InUtc_WithARecurrenceIdOnEveryInstance() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:daily
            DTSTART;TZID=America/New_York:20060102T120000
            DURATION:PT1H
            RRULE:FREQ=DAILY;COUNT=5
            SUMMARY:Daily
            END:VEVENT
            BEGIN:VEVENT
            UID:daily
            RECURRENCE-ID;TZID=America/New_York:20060104T120000
            DTSTART;TZID=America/New_York:20060104T140000
            DURATION:PT1H
            SUMMARY:Moved
            END:VEVENT
            """);

        var response = await harness.SendAsync("REPORT", CalDavConformance.Path, CalDavConformance.QueryBody(
            CalDavConformance.Events(CalDavConformance.Range("20060103T000000Z", "20060105T000000Z")), "",
            "<D:prop>" + Expand("20060103T000000Z", "20060105T000000Z") + "</D:prop>"), headers: CalDavConformance.Depth1);
        var text = Unfold(response.Prop(Href + "daily.ics", DavNames.C.CalendarData)!.Text);

        Count(text, "BEGIN:VEVENT").Should().Be(2);
        text.Should().Contain("RECURRENCE-ID:20060103T170000Z").And.Contain("DTSTART:20060103T170000Z")
            .And.Contain("RECURRENCE-ID:20060104T170000Z").And.Contain("DTSTART:20060104T190000Z").And.Contain("SUMMARY:Moved")
            .And.Contain("DURATION:PT1H");
        text.Should().NotContain("DTSTART:20060104T170000Z").And.NotContain("RRULE").And.NotContain("VTIMEZONE").And.NotContain("TZID");
    }

    /// <summary>
    /// Apple E-06, E-07, E-10; sabre X09, X14; Radicale E08; Xandikos E22: all-day instances stay DATE values with
    /// RECURRENCE-ID;VALUE=DATE, a date override moved a week keeps its date RECURRENCE-ID, and a single event gets none.
    /// </summary>
    [Fact]
    public async Task ExpandedAllDayEvents_KeepTheirDates() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:days
            DTSTART;VALUE=DATE:20271017
            RRULE:FREQ=DAILY;COUNT=2
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:single
            DTSTART;VALUE=DATE:20271015
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:moved-day
            DTSTART;VALUE=DATE:20060102
            RRULE:FREQ=DAILY;COUNT=3
            END:VEVENT
            BEGIN:VEVENT
            UID:moved-day
            RECURRENCE-ID;VALUE=DATE:20060103
            DTSTART;VALUE=DATE:20060110
            END:VEVENT
            """);

        var days = await DataAsync(harness, "days", Expand("20271014T000000Z", "20271020T000000Z"));
        var single = await DataAsync(harness, "single", Expand("20271014T000000Z", "20271020T000000Z"));
        var moved = await DataAsync(harness, "moved-day", Expand("20060101T000000Z", "20060111T235959Z"));

        days.Should().Contain("DTSTART;VALUE=DATE:20271017").And.Contain("DTSTART;VALUE=DATE:20271018")
            .And.Contain("RECURRENCE-ID;VALUE=DATE:20271017").And.Contain("RECURRENCE-ID;VALUE=DATE:20271018");
        single.Should().Contain("DTSTART;VALUE=DATE:20271015").And.NotContain("RECURRENCE-ID");
        Count(moved, "BEGIN:VEVENT").Should().Be(3);
        moved.Should().Contain("RECURRENCE-ID;VALUE=DATE:20060103").And.Contain("DTSTART;VALUE=DATE:20060110")
            .And.Contain("RECURRENCE-ID;VALUE=DATE:20060104").And.NotContain("DTSTART;VALUE=DATE:20060103");
    }

    /// <summary>sabre X03 (testExpandRecurringByDayEvent): a master whose own start lies before the window is not emitted.</summary>
    [Fact]
    public async Task MasterStartingBeforeTheWindow_IsNotEmitted() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:tu-th
            DTSTART;TZID=Europe/Berlin:20120207T181500
            DTEND;TZID=Europe/Berlin:20120207T191500
            RRULE:FREQ=WEEKLY;BYDAY=TU,TH
            END:VEVENT
            """);

        var text = await DataAsync(harness, "tu-th", Expand("20120210T230000Z", "20120217T225959Z"));

        Count(text, "BEGIN:VEVENT").Should().Be(2);
        text.Should().Contain("DTSTART:20120214T171500Z").And.Contain("DTSTART:20120216T171500Z").And.NotContain("20120207");
    }

    /// <summary>
    /// sabre X18, X19, X20 (vobject MissingOverriddenTest, IncorrectExpandTest, ExpandOverrideFirstEvent); Radicale event6:
    /// a moved override leaves no ghost at its original slot, an override moved past the rule's end is still emitted, and
    /// an override of the first instance replaces the master's own start.
    /// </summary>
    [Fact]
    public async Task MovedOverrides_LeaveNoGhostAtTheirOriginalSlot() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:moved-later
            DTSTART:20130711T100000Z
            DTEND:20130711T110000Z
            RRULE:FREQ=DAILY;COUNT=2
            END:VEVENT
            BEGIN:VEVENT
            UID:moved-later
            RECURRENCE-ID:20130712T100000Z
            DTSTART:20130719T100000Z
            DTEND:20130719T110000Z
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:past-end
            DTSTART:20130727T100000Z
            DTEND:20130727T110000Z
            RRULE:FREQ=DAILY;COUNT=2
            END:VEVENT
            BEGIN:VEVENT
            UID:past-end
            RECURRENCE-ID:20130728T100000Z
            DTSTART:20140101T100000Z
            DTEND:20140101T110000Z
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:first
            DTSTART:20140803T100000Z
            DTEND:20140803T110000Z
            RRULE:FREQ=WEEKLY;COUNT=3
            END:VEVENT
            BEGIN:VEVENT
            UID:first
            RECURRENCE-ID:20140803T100000Z
            DTSTART:20140803T150000Z
            DTEND:20140803T160000Z
            SUMMARY:Overridden
            END:VEVENT
            """);

        var later = await DataAsync(harness, "moved-later", Expand("20130701T000000Z", "20130801T000000Z"));
        var pastEnd = await DataAsync(harness, "past-end", Expand("20110101T000000Z", "20150101T000000Z"));
        var first = await DataAsync(harness, "first", Expand("20140801T000000Z", "20140901T000000Z"));

        Count(later, "BEGIN:VEVENT").Should().Be(2);
        later.Should().Contain("DTSTART:20130719T100000Z").And.Contain("RECURRENCE-ID:20130712T100000Z").And.NotContain("DTSTART:20130712");
        Count(pastEnd, "BEGIN:VEVENT").Should().Be(2);
        pastEnd.Should().Contain("DTSTART:20130727T100000Z").And.Contain("DTSTART:20140101T100000Z").And.NotContain("DTSTART:20130728");
        Count(first, "BEGIN:VEVENT").Should().Be(3);
        first.Should().Contain("DTSTART:20140803T150000Z").And.NotContain("DTSTART:20140803T100000Z");
        first.IndexOf("SUMMARY:Overridden", StringComparison.Ordinal).Should().BeLessThan(first.IndexOf("DTSTART:20140810", StringComparison.Ordinal),
            "instances come in date order");
    }

    /// <summary>
    /// Apple E-12; sabre X15, X21; Radicale E09; Xandikos E23, E24: an EXDATE equal to DTSTART removes the first instance, an
    /// RDATE adds one, the expanded instances carry no RRULE, RDATE or EXDATE, and a master without instances expands to no
    /// component at all.
    /// </summary>
    [Fact]
    public async Task Expand_AppliesExdateAndRdate_AndStripsThem() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:set
            DTSTART:20280101T170000Z
            DURATION:PT1H
            RRULE:FREQ=DAILY;COUNT=4
            EXDATE:20280101T170000Z
            EXDATE:20280103T170000Z
            RDATE:20280110T170000Z
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:none-left
            DTSTART:20280101T170000Z
            DURATION:PT1H
            RRULE:FREQ=WEEKLY;COUNT=1
            EXDATE:20280101T170000Z
            END:VEVENT
            """);

        var set = await DataAsync(harness, "set", Expand("20280101T000000Z", "20280201T000000Z"));
        var noneLeft = await DataAsync(harness, "none-left", Expand("20280101T000000Z", "20280201T000000Z"));

        Count(set, "BEGIN:VEVENT").Should().Be(3);
        set.Should().Contain("RECURRENCE-ID:20280102T170000Z").And.Contain("RECURRENCE-ID:20280104T170000Z").And.Contain("RECURRENCE-ID:20280110T170000Z");
        set.Should().NotContain("20280101T170000Z").And.NotContain("20280103T170000Z").And.NotContain("RRULE").And.NotContain("RDATE")
            .And.NotContain("EXDATE");
        noneLeft.Should().NotContain("BEGIN:VEVENT");
    }

    /// <summary>
    /// Radicale E02, E03; Xandikos E27; sabre R57: an instance overlapping the window is expanded even when it starts before
    /// it, a one-second window inside an instance holds exactly it, and an instance ending at the window's start or starting
    /// at its end is left out.
    /// </summary>
    [Fact]
    public async Task ExpandWindow_IsHalfOpen_AndKeepsOverlappingInstances() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:three
            DTSTART:20240115T100000Z
            DTEND:20240115T110000Z
            RRULE:FREQ=DAILY;COUNT=3
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:days
            DTSTART;VALUE=DATE:20110404
            RRULE:FREQ=DAILY;COUNT=3
            END:VEVENT
            """);

        Count(await DataAsync(harness, "three", Expand("20240115T103000Z", "20240115T103001Z")), "BEGIN:VEVENT").Should().Be(1);
        Count(await DataAsync(harness, "three", Expand("20240115T110000Z", "20240116T100000Z")), "BEGIN:VEVENT").Should().Be(0);
        Count(await DataAsync(harness, "three", Expand("20240115T100000Z", "20240116T100000Z")), "BEGIN:VEVENT").Should().Be(1);
        var days = await DataAsync(harness, "days", Expand("20110405T000000Z", "20110406T000000Z"));
        Count(days, "BEGIN:VEVENT").Should().Be(1);
        days.Should().Contain("DTSTART;VALUE=DATE:20110405");
    }

    /// <summary>
    /// sabre X04, X05 (ExpandEventsFloatingTimeTest through calendar-query and calendar-multiget); Apple E-14: floating times
    /// expand to UTC through the calendar's time zone.
    /// </summary>
    [Fact]
    public async Task FloatingTimes_ExpandThroughTheCalendarZone() {
        await using var harness = await CalDavConformance.CalendarAsync(CalDavConformance.Zone("Europe/Berlin"), """
            BEGIN:VEVENT
            UID:floating
            DTSTART:20141108T053000
            DTEND:20141108T073000
            END:VEVENT
            """);

        var text = await DataAsync(harness, "floating", Expand("20141108T000000Z", "20141109T000000Z"));

        text.Should().Contain("DTSTART:20141108T043000Z").And.Contain("DTEND:20141108T063000Z");
    }

    /// <summary>
    /// Apple E-08 and RFC 4791 §9.6.5 / §7.3: a CALDAV:timezone in the request is the zone floating times are expanded in,
    /// as it is the zone the filter used.
    /// </summary>
    [Fact]
    public async Task FloatingTimes_ExpandThroughTheRequestsTimezone() {
        await using var harness = await CalDavConformance.CalendarAsync(CalDavConformance.Zone("Europe/Berlin"), """
            BEGIN:VEVENT
            UID:floating
            DTSTART:20141108T053000
            DTEND:20141108T073000
            END:VEVENT
            """);
        var newYork = await CalDavConformance.TimezoneAsync(CalDavConformance.Zone("America/New_York"));

        var response = await harness.SendAsync("REPORT", CalDavConformance.Path, CalDavConformance.QueryBody(
            CalDavConformance.Events(CalDavConformance.Range("20141108T000000Z", "20141109T000000Z")), newYork,
            "<D:prop>" + Expand("20141108T000000Z", "20141109T000000Z") + "</D:prop>"), headers: CalDavConformance.Depth1);

        response.Prop(Href + "floating.ics", DavNames.C.CalendarData)!.Text.Should().Contain("DTSTART:20141108T103000Z");
    }

    /// <summary>sabre M02 (testCalendarMultiGetReportExpand): the ETag names the stored object, not the expanded body.</summary>
    [Fact]
    public async Task ExpandedData_KeepsTheStoredObjectsETag() {
        await using var harness = await CalDavConformance.CalendarAsync(null,
            "BEGIN:VEVENT\nUID:weekly\nDTSTART:20110101T100000Z\nDURATION:PT1H\nRRULE:FREQ=WEEKLY\nEND:VEVENT");
        string Body(string data) =>
            $"<C:calendar-multiget {Ns}><D:prop><D:getetag/>{data}</D:prop><D:href>{Href}weekly.ics</D:href></C:calendar-multiget>";

        var plain = await harness.SendAsync("REPORT", CalDavConformance.Path, Body("<C:calendar-data/>"));
        var expanded = await harness.SendAsync("REPORT", CalDavConformance.Path, Body(Expand("20110101T000000Z", "20111231T235959Z")));

        expanded.Prop(Href + "weekly.ics", DavNames.D.GetETag)!.Text.Should().Be(plain.Prop(Href + "weekly.ics", DavNames.D.GetETag)!.Text);
        expanded.Prop(Href + "weekly.ics", DavNames.C.CalendarData)!.Text.Should().NotContain("RRULE");
    }

    /// <summary>
    /// Radicale E05 (event_weekly_rrule, US/Eastern, whose DST started on the first Sunday of April in 2006): each instance
    /// takes its own offset. The canonical America/New_York is used, because the publisher writes the zone from the system's
    /// time-zone database, and Linux images without tzdata's backward links do not know the US/* aliases.
    /// </summary>
    [Fact]
    public async Task ExpandAcrossADaylightSavingChange_GivesEachInstanceItsOffset() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:weekly
            DTSTART;TZID=America/New_York:20060321T150000
            DURATION:PT1H
            RRULE:FREQ=WEEKLY;COUNT=5
            END:VEVENT
            """);

        var text = await DataAsync(harness, "weekly", Expand("20060320T000000Z", "20060414T000000Z"));

        text.Should().Contain("DTSTART:20060321T200000Z").And.Contain("DTSTART:20060328T200000Z")
            .And.Contain("DTSTART:20060404T190000Z").And.Contain("DTSTART:20060411T190000Z");
    }

    /// <summary>Xandikos E25 (test_expand_recurring_vtodo_with_due): a to-do's DUE moves with each instance.</summary>
    [Fact]
    public async Task ExpandedTodo_MovesItsDueWithTheInstance() {
        await using var harness = await CalDavConformance.CalendarAsync(null,
            "BEGIN:VTODO\nUID:monthly\nDTSTART:20000112T120000Z\nDUE:20000112T130000Z\nRRULE:FREQ=MONTHLY\nEND:VTODO");

        var text = await DataAsync(harness, "monthly", Expand("20000201T000000Z", "20000301T000000Z"));

        Count(text, "BEGIN:VTODO").Should().Be(1);
        text.Should().Contain("DTSTART:20000212T120000Z").And.Contain("DUE:20000212T130000Z").And.Contain("RECURRENCE-ID:20000212T120000Z");
    }

    /// <summary>
    /// Radicale E14 (event_mixed_recurrence_id_dt_type: Radicale answers 400). Pins Elarion's choice: an override whose
    /// RECURRENCE-ID is a DATE replaces that day's instance of a DATE-TIME series, and the expansion answers normally.
    /// </summary>
    [Fact]
    public async Task OverrideWithADateRecurrenceId_ReplacesThatDaysInstance() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:mixed
            DTSTART:20060102T120000Z
            DURATION:PT1H
            RRULE:FREQ=DAILY;COUNT=3
            END:VEVENT
            BEGIN:VEVENT
            UID:mixed
            RECURRENCE-ID;VALUE=DATE:20060103
            DTSTART:20060103T150000Z
            DURATION:PT1H
            SUMMARY:Changed
            END:VEVENT
            """);

        var text = await DataAsync(harness, "mixed", Expand("20060101T000000Z", "20060110T000000Z"));

        Count(text, "BEGIN:VEVENT").Should().Be(3);
        text.Should().Contain("SUMMARY:Changed").And.Contain("DTSTART:20060103T150000Z").And.NotContain("DTSTART:20060103T120000Z");
    }

    public static TheoryData<string> InvalidExpands => new() {
        "<C:expand start=\"20110101T000000Z\"/>",
        "<C:expand end=\"20110101T000000Z\"/>",
        "<C:expand start=\"20120101T000000Z\" end=\"20110101T000000Z\"/>",
        "<C:expand start=\"20240101T000000Z\" end=\"20240101T000000Z\"/>",
    };

    /// <summary>
    /// sabre Q90, X25 (400 for an expand without end, without start, or ending before its start, in query and multiget);
    /// Xandikos E20 (an empty range is an error, not a crash). RFC 4791 §9.6.5 requires both attributes.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidExpands))]
    public async Task ExpandWithoutAValidRange_Answers400(string expand) {
        await using var harness = await CalDavConformance.CalendarAsync(null,
            "BEGIN:VEVENT\nUID:weekly\nDTSTART:20110101T100000Z\nDURATION:PT1H\nRRULE:FREQ=WEEKLY\nEND:VEVENT");

        var response = await harness.SendAsync("REPORT", CalDavConformance.Path,
            $"<C:calendar-multiget {Ns}><D:prop><C:calendar-data>{expand}</C:calendar-data></D:prop><D:href>{Href}weekly.ics</D:href></C:calendar-multiget>");

        response.Status.Should().Be(400, response.Body);
    }

    /// <summary>
    /// sabre X24 (MaxInstancesExceededException); Radicale E15 (400); Cyrus E15 (a 422 propstat). Pins Elarion's choice: the
    /// expansion keeps the first instances up to <c>CalDavOptions.CalendarExpand.MaxOccurrences</c> and answers 207.
    /// </summary>
    [Fact]
    public async Task ExpandBeyondTheOccurrenceCap_KeepsTheFirstInstances() {
        await using var harness = CalDavTestHarness.Create(services =>
            services.Configure<CalDavOptions>(o => o.CalendarExpand = new CalendarExpandOptions { MaxOccurrences = 3 }));
        harness.Directory.Override = [new PublishedCalendarInfo("c", "Conformance")];
        await CalDavConformance.PublishAsync(harness, "BEGIN:VEVENT\nUID:daily\nDTSTART:20110101T100000Z\nDURATION:PT1H\nRRULE:FREQ=DAILY\nEND:VEVENT");

        var text = await DataAsync(harness, "daily", Expand("20110101T000000Z", "20110111T000000Z"));

        Count(text, "BEGIN:VEVENT").Should().Be(3);
        text.Should().Contain("DTSTART:20110101T100000Z").And.Contain("DTSTART:20110103T100000Z").And.NotContain("DTSTART:20110104");
    }

    /// <summary>
    /// Xandikos E29, E30 (test_limit_recurrence_set, _thisandfuture, _date_values): the master stays, with only the
    /// overrides whose instance overlaps the range, a THISANDFUTURE override and a date override included.
    /// </summary>
    [Fact]
    public async Task LimitRecurrenceSet_KeepsTheOverridesOverlappingTheRange() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:weekly
            DTSTART:20240101T100000Z
            DURATION:PT1H
            RRULE:FREQ=WEEKLY
            END:VEVENT
            BEGIN:VEVENT
            UID:weekly
            RECURRENCE-ID:20240115T100000Z
            DTSTART:20240116T100000Z
            DURATION:PT1H
            SUMMARY:January
            END:VEVENT
            BEGIN:VEVENT
            UID:weekly
            RECURRENCE-ID;RANGE=THISANDFUTURE:20240212T100000Z
            DTSTART:20240212T140000Z
            DURATION:PT1H
            SUMMARY:February onwards
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:days
            DTSTART;VALUE=DATE:20240101
            RRULE:FREQ=DAILY
            END:VEVENT
            BEGIN:VEVENT
            UID:days
            RECURRENCE-ID;VALUE=DATE:20240201
            DTSTART;VALUE=DATE:20240202
            SUMMARY:Moved day
            END:VEVENT
            """);
        string Limit(string start, string end) => $"<C:calendar-data><C:limit-recurrence-set start=\"{start}\" end=\"{end}\"/></C:calendar-data>";

        var february = await DataAsync(harness, "weekly", Limit("20240201T000000Z", "20240301T000000Z"));
        var january = await DataAsync(harness, "weekly", Limit("20240101T000000Z", "20240201T000000Z"));
        var days = await DataAsync(harness, "days", Limit("20240201T000000Z", "20240301T000000Z"));

        february.Should().Contain("RRULE:FREQ=WEEKLY").And.Contain("SUMMARY:February onwards").And.NotContain("SUMMARY:January");
        january.Should().Contain("RRULE:FREQ=WEEKLY").And.Contain("SUMMARY:January").And.NotContain("SUMMARY:February onwards");
        days.Should().Contain("SUMMARY:Moved day");
    }

    private const string AlarmedEvent = """
        BEGIN:VEVENT
        UID:alarmed
        DTSTAMP:20260101T000000Z
        DTSTART;TZID=Europe/Vienna:20261118T140000
        DTEND;TZID=Europe/Vienna:20261118T160000
        RRULE:FREQ=DAILY;COUNT=3
        SUMMARY:Planning
        BEGIN:VALARM
        ACTION:DISPLAY
        DESCRIPTION:Soon
        TRIGGER:-PT15M
        END:VALARM
        END:VEVENT
        """;

    private static string Partial(string comp) => $"<C:calendar-data>{comp}</C:calendar-data>";

    /// <summary>
    /// Xandikos E32 (test_comp, test_allprop, test_allcomp); Apple Q-22, M-02, M-03, M-04: a comp without prop keeps no
    /// properties, allprop and allcomp keep everything below them, and a sub-component not named is dropped.
    /// </summary>
    [Fact]
    public async Task PartialCalendarData_SelectsComponentsAndProperties() {
        await using var harness = await CalDavConformance.CalendarAsync(null, AlarmedEvent);

        var bare = await DataAsync(harness, "alarmed", Partial("<C:comp name=\"VCALENDAR\"/>"));
        var calendarOnly = await DataAsync(harness, "alarmed", Partial("<C:comp name=\"VCALENDAR\"><C:allprop/></C:comp>"));
        var zonesOnly = await DataAsync(harness, "alarmed", Partial("<C:comp name=\"VCALENDAR\"><C:allprop/><C:comp name=\"VTIMEZONE\"><C:allprop/><C:allcomp/></C:comp></C:comp>"));
        var emptyZone = await DataAsync(harness, "alarmed", Partial("<C:comp name=\"VCALENDAR\"><C:comp name=\"VEVENT\"><C:prop name=\"UID\"/></C:comp><C:comp name=\"VTIMEZONE\"/></C:comp>"));
        var withAlarms = await DataAsync(harness, "alarmed", Partial(
            "<C:comp name=\"VCALENDAR\"><C:allprop/><C:comp name=\"VEVENT\"><C:prop name=\"SUMMARY\"/><C:prop name=\"UID\"/><C:allcomp/></C:comp></C:comp>"));

        bare.Should().Contain("BEGIN:VCALENDAR").And.NotContain("VERSION").And.NotContain("BEGIN:VEVENT").And.NotContain("BEGIN:VTIMEZONE");
        calendarOnly.Should().Contain("VERSION:2.0").And.Contain("PRODID").And.NotContain("BEGIN:VEVENT").And.NotContain("BEGIN:VTIMEZONE");
        zonesOnly.Should().Contain("BEGIN:VTIMEZONE").And.Contain("TZID:Europe/Vienna").And.NotContain("BEGIN:VEVENT");
        emptyZone.Should().Contain("BEGIN:VTIMEZONE").And.NotContain("TZID:").And.Contain("UID:alarmed").And.NotContain("DTSTAMP")
            .And.NotContain("SUMMARY");
        withAlarms.Should().Contain("BEGIN:VALARM").And.Contain("TRIGGER:-PT15M").And.Contain("SUMMARY:Planning").And.NotContain("DTSTART")
            .And.NotContain("RRULE");
    }

    /// <summary>Apple M-05; RFC 4791 §9.6.4: <c>novalue="yes"</c> returns the property without its value.</summary>
    [Fact]
    public async Task PartialCalendarData_WithNoValue_ReturnsThePropertyWithoutItsValue() {
        await using var harness = await CalDavConformance.CalendarAsync(null, AlarmedEvent);

        var text = await DataAsync(harness, "alarmed", Partial(
            "<C:comp name=\"VCALENDAR\"><C:comp name=\"VEVENT\"><C:prop name=\"UID\"/><C:prop name=\"SUMMARY\" novalue=\"yes\"/></C:comp></C:comp>"));

        text.Should().Contain("SUMMARY:\r\n").And.NotContain("Planning");
    }

    /// <summary>Cyrus E17 (calendar_query); Apple E-02: expand combined with a comp/prop selection keeps the RECURRENCE-IDs asked for.</summary>
    [Fact]
    public async Task ExpandWithAPartialSelection_KeepsTheNamedPropertiesOfEachInstance() {
        await using var harness = await CalDavConformance.CalendarAsync(null, AlarmedEvent);

        var text = await DataAsync(harness, "alarmed", Expand("20261118T000000Z", "20261121T000000Z",
            "<C:comp name=\"VCALENDAR\"><C:prop name=\"VERSION\"/><C:comp name=\"VEVENT\"><C:prop name=\"SUMMARY\"/><C:prop name=\"UID\"/>"
            + "<C:prop name=\"DTSTART\"/><C:prop name=\"RECURRENCE-ID\"/></C:comp></C:comp>"));

        Count(text, "BEGIN:VEVENT").Should().Be(3);
        text.Should().Contain("RECURRENCE-ID:20261118T130000Z").And.Contain("DTSTART:20261120T130000Z").And.Contain("SUMMARY:Planning");
        text.Should().NotContain("DTSTAMP").And.NotContain("RRULE").And.NotContain("DTEND").And.NotContain("BEGIN:VALARM").And.NotContain("PRODID");
    }

    /// <summary>python-caldav QY-24 (Bedework answered the master alone): calendar-data without children is the whole object, overrides included.</summary>
    [Fact]
    public async Task CalendarDataWithoutChildren_IsTheWholeObject() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:series
            DTSTART:20260105T100000Z
            DURATION:PT1H
            RRULE:FREQ=DAILY;COUNT=3
            END:VEVENT
            BEGIN:VEVENT
            UID:series
            RECURRENCE-ID:20260106T100000Z
            DTSTART:20260106T150000Z
            DURATION:PT1H
            END:VEVENT
            """);

        var response = await harness.SendAsync("REPORT", CalDavConformance.Path,
            CalDavConformance.QueryBody(CalDavConformance.Events(), "", "<D:prop><C:calendar-data/></D:prop>"), headers: CalDavConformance.Depth1);
        var text = response.Prop(Href + "series.ics", DavNames.C.CalendarData)!.Text;

        Count(text, "BEGIN:VEVENT").Should().Be(2);
        text.Should().Contain("RRULE:FREQ=DAILY").And.Contain("RECURRENCE-ID:20260106T100000Z");
    }

    /// <summary>sabre M03 (ICSExportPluginTest testBeforeMethod); python-caldav CT-21: a feed holds one VTIMEZONE per zone its objects use.</summary>
    [Fact]
    public async Task Feed_HoldsOneVTimezonePerZone() {
        await using var host = await CalDavHost.StartAsync();
        var newYork = CalDavConformance.Zone("America/New_York");
        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("a", new DateTime(2026, 12, 1, 6, 0, 0), TimeSpan.FromHours(8)));
        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("b", new DateTime(2026, 12, 2, 6, 0, 0), TimeSpan.FromHours(8)));
        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("c", new DateTime(2026, 12, 3, 9, 0, 0), TimeSpan.FromHours(1), zone: newYork));
        await host.PublishAsync("shifts", "u-alice", new CalendarEvent {
            Uid = "d", Timestamp = CalDavTestData.Stamp, Start = CalendarDateTime.Utc(new DateTimeOffset(2026, 12, 4, 9, 0, 0, TimeSpan.Zero)),
        });
        await using var scope = host.App.Services.CreateAsyncScope();
        var feed = await scope.ServiceProvider.GetRequiredService<IAppPasswords>()
            .CreateAsync("alice", "Outlook", AppPasswordScope.CalendarFeed(new CalDavAddress("shifts", "u-alice")), TestContext.Current.CancellationToken);

        var body = (await host.SendAsync("GET", host.App.Services.GetRequiredService<CalDavLinks>().FeedPath(feed.Value))).Dav.Body;

        Count(body, "BEGIN:VTIMEZONE").Should().Be(2);
        Count(body, "TZID:Europe/Vienna\r\n").Should().Be(1);
        Count(body, "TZID:America/New_York\r\n").Should().Be(1);
        Count(body, "BEGIN:VEVENT").Should().Be(4);
    }
}
