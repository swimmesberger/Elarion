using AwesomeAssertions;
using Elarion.CalDav;
using Elarion.Calendars;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>The calendar reports (spec 0006 FR-019, FR-022): multiget, query with every filter and time-range rule, expand, free-busy.</summary>
public sealed class CalDavReportTests {
    private const string Calendar = "/calendars/alice/shifts.u-alice/";
    private const string Href = "/dav/calendars/alice/shifts.u-alice/";
    private const string Ns = CalDavTestHarness.Namespaces;

    private static readonly Dictionary<string, string> Depth1 = new() { ["Depth"] = "1" };

    private static async Task<CalDavTestHarness> WithWeekAsync() {
        var harness = CalDavTestHarness.Create();
        var weekly = CalDavTestData.Event("weekly", new DateTime(2025, 11, 3, 9, 0, 0), TimeSpan.FromHours(1), "Stand-up");
        weekly.RecurrenceRules.Add(new CalendarRecurrenceRule { Frequency = CalendarFrequency.Weekly });
        await harness.PublishAsync("shifts", "u-alice", weekly);
        await harness.PublishAsync("shifts", "u-alice", CalDavTestData.Event("last-month", new DateTime(2026, 10, 15, 8, 0, 0), TimeSpan.FromHours(2), "Review"));
        await harness.PublishAsync("shifts", "u-alice", CalDavTestData.Event("this-week", new DateTime(2026, 11, 18, 14, 0, 0), TimeSpan.FromHours(2), "Planning"));
        return harness;
    }

    [Fact]
    public async Task GivenAWeeklyEndlessEventFromAYearAgo_WhenQueryingOneWeek_ThenItIsInTheAnswerAndAnEventFromLastMonthIsNot() {
        await using var harness = await WithWeekAsync();

        var response = await harness.SendAsync("REPORT", Calendar, CalDavTestHarness.Query(CalDavTestHarness.EventsIn("20261116T000000Z", "20261123T000000Z")),
            headers: Depth1);

        response.Status.Should().Be(207);
        response.Responses.Keys.Should().BeEquivalentTo(Href + "weekly.ics", Href + "this-week.ics");
        response.Prop(Href + "weekly.ics", DavNames.C.CalendarData)!.Text.Should().Contain("RRULE:FREQ=WEEKLY");
    }

    [Fact]
    public async Task Multiget_AnswersTheNamedObjectsInOrder_AndAMissingOneAs404() {
        await using var harness = await WithWeekAsync();
        var body = $"<C:calendar-multiget {Ns}><D:prop><D:getetag/><C:calendar-data/></D:prop>"
                   + $"<D:href>{Href}this-week.ics</D:href><D:href>https://host.example{Href}weekly.ics</D:href><D:href>{Href}gone.ics</D:href>"
                   + "<D:href>/dav/calendars/alice/other.k/x.ics</D:href></C:calendar-multiget>";

        var response = await harness.SendAsync("REPORT", Calendar, body);

        response.Status.Should().Be(207);
        var hrefs = response.Xml.ChildrenNamed(DavNames.D.Response).Select(static r => r.Child(DavNames.D.Href)!.Text).ToList();
        hrefs.Should().Equal(Href + "this-week.ics", Href + "weekly.ics", Href + "gone.ics", "/dav/calendars/alice/other.k/x.ics");
        response.Prop(Href + "this-week.ics", DavNames.C.CalendarData)!.Text.Should().StartWith("BEGIN:VCALENDAR\r\n");
        response.Responses[Href + "gone.ics"].Child(DavNames.D.Status)!.Text.Should().Contain("404");
    }

    [Fact]
    public async Task Multiget_WithMoreHrefsThanTheLimit_Answers413() {
        await using var harness = CalDavTestHarness.Create(webDav: o => o.MaxMultigetHrefs = 2);
        var body = $"<C:calendar-multiget {Ns}><D:prop><D:getetag/></D:prop><D:href>a</D:href><D:href>b</D:href><D:href>c</D:href></C:calendar-multiget>";

        (await harness.SendAsync("REPORT", Calendar, body)).Status.Should().Be(413);
    }

    [Fact]
    public async Task Query_WithPropAndParamFilters_MatchesByText() {
        await using var harness = await WithWeekAsync();

        var summary = await harness.SendAsync("REPORT", Calendar, CalDavTestHarness.Query(
            "<C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"VEVENT\"><C:prop-filter name=\"SUMMARY\"><C:text-match>PLAN</C:text-match>"
            + "</C:prop-filter></C:comp-filter></C:comp-filter>"), headers: Depth1);
        var negated = await harness.SendAsync("REPORT", Calendar, CalDavTestHarness.Query(
            "<C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"VEVENT\"><C:prop-filter name=\"SUMMARY\">"
            + "<C:text-match negate-condition=\"yes\">plan</C:text-match></C:prop-filter></C:comp-filter></C:comp-filter>"), headers: Depth1);
        var octet = await harness.SendAsync("REPORT", Calendar, CalDavTestHarness.Query(
            "<C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"VEVENT\"><C:prop-filter name=\"SUMMARY\">"
            + "<C:text-match collation=\"i;octet\">plan</C:text-match></C:prop-filter></C:comp-filter></C:comp-filter>"), headers: Depth1);
        var param = await harness.SendAsync("REPORT", Calendar, CalDavTestHarness.Query(
            "<C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"VEVENT\"><C:prop-filter name=\"DTSTART\"><C:param-filter name=\"TZID\">"
            + "<C:text-match>vienna</C:text-match></C:param-filter></C:prop-filter></C:comp-filter></C:comp-filter>"), headers: Depth1);
        var undefined = await harness.SendAsync("REPORT", Calendar, CalDavTestHarness.Query(
            "<C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"VEVENT\"><C:prop-filter name=\"RRULE\"><C:is-not-defined/></C:prop-filter>"
            + "</C:comp-filter></C:comp-filter>"), headers: Depth1);

        summary.Responses.Keys.Should().Equal(Href + "this-week.ics");
        negated.Responses.Keys.Should().BeEquivalentTo(Href + "weekly.ics", Href + "last-month.ics");
        octet.Responses.Should().BeEmpty();
        param.Responses.Should().HaveCount(3);
        undefined.Responses.Keys.Should().BeEquivalentTo(Href + "this-week.ics", Href + "last-month.ics");
    }

    [Fact]
    public async Task Query_WithAnInvalidFilter_Answers403ValidFilter() {
        await using var harness = await WithWeekAsync();

        var response = await harness.SendAsync("REPORT", Calendar, CalDavTestHarness.Query("<C:comp-filter name=\"VEVENT\"/>"), headers: Depth1);
        var collation = await harness.SendAsync("REPORT", Calendar, CalDavTestHarness.Query(
            "<C:comp-filter name=\"VCALENDAR\"><C:prop-filter name=\"X\"><C:text-match collation=\"i;klingon\">a</C:text-match></C:prop-filter></C:comp-filter>"),
            headers: Depth1);

        response.Status.Should().Be(403);
        response.Xml.Child(DavNames.C.ValidFilter).Should().NotBeNull();
        collation.Status.Should().Be(403);
    }

    [Fact]
    public async Task TimeRange_FollowsTheRfc4791Table() {
        await using var harness = CalDavTestHarness.Create();
        var utc = TimeZoneInfo.Utc;
        var zeroLength = CalDavTestData.Event("zero", new DateTime(2026, 12, 1, 10, 0, 0), TimeSpan.Zero, zone: utc);
        zeroLength.End = null;
        var allDay = new CalendarEvent {
            Uid = "allday", Timestamp = CalDavTestData.Stamp, Summary = "Holiday", Start = CalendarDateTime.FromDate(new DateOnly(2026, 12, 2)),
        };
        var withDuration = new CalendarEvent {
            Uid = "duration", Timestamp = CalDavTestData.Stamp, Start = CalendarDateTime.Utc(new DateTimeOffset(2026, 12, 3, 10, 0, 0, TimeSpan.Zero)),
            Duration = TimeSpan.FromHours(2),
        };
        var todoDue = new CalendarTodo { Uid = "todo-due", Timestamp = CalDavTestData.Stamp, Due = CalendarDateTime.Utc(new DateTimeOffset(2026, 12, 4, 12, 0, 0, TimeSpan.Zero)) };
        var todoNone = new CalendarTodo { Uid = "todo-none", Timestamp = CalDavTestData.Stamp, Summary = "Someday" };
        foreach (var item in new CalendarObject[] { zeroLength, allDay, withDuration, todoDue, todoNone }) await harness.PublishAsync("teams", "red", item);
        const string team = "/calendars/alice/teams.red/";
        const string teamHref = "/dav/calendars/alice/teams.red/";

        async Task<IEnumerable<string>> QueryAsync(string comp, string start, string end) =>
            (await harness.SendAsync("REPORT", team, CalDavTestHarness.Query(
                $"<C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"{comp}\"><C:time-range start=\"{start}\" end=\"{end}\"/></C:comp-filter></C:comp-filter>",
                "<D:getetag/>"), headers: Depth1)).Responses.Keys.Select(static k => k[teamHref.Length..]);

        (await QueryAsync("VEVENT", "20261201T100000Z", "20261201T110000Z")).Should().Contain("zero.ics", "a zero-length event at the range's start");
        (await QueryAsync("VEVENT", "20261201T090000Z", "20261201T100000Z")).Should().NotContain("zero.ics", "the range ends where it starts");
        (await QueryAsync("VEVENT", "20261202T230000Z", "20261203T000000Z")).Should().Contain("allday.ics", "a date lasts one day");
        (await QueryAsync("VEVENT", "20261203T115900Z", "20261203T130000Z")).Should().Contain("duration.ics");
        (await QueryAsync("VEVENT", "20261203T120000Z", "20261203T130000Z")).Should().NotContain("duration.ics", "the end is exclusive");
        (await QueryAsync("VTODO", "20261204T120000Z", "20261204T130000Z")).Should().Equal(["todo-none.ics"], "a to-do with only DUE needs start < DUE");
        (await QueryAsync("VTODO", "20261204T000000Z", "20261204T115959Z")).Should().Equal(["todo-none.ics"], "and end >= DUE");
        (await QueryAsync("VTODO", "20261204T000000Z", "20261204T120000Z")).Should().BeEquivalentTo("todo-due.ics", "todo-none.ics");
    }

    [Fact]
    public async Task FloatingTimes_AreEvaluatedInTheCalendarsTimeZone() {
        await using var harness = CalDavTestHarness.Create();
        var floating = new CalendarEvent {
            Uid = "floating", Timestamp = CalDavTestData.Stamp,
            Start = CalendarDateTime.Floating(new DateTime(2026, 12, 1, 9, 0, 0)), End = CalendarDateTime.Floating(new DateTime(2026, 12, 1, 10, 0, 0)),
        };
        await harness.PublishAsync("shifts", "u-alice", floating);

        // 09:00 in Vienna is 08:00 UTC in December.
        var inVienna = await harness.SendAsync("REPORT", Calendar, CalDavTestHarness.Query(CalDavTestHarness.EventsIn("20261201T080000Z", "20261201T083000Z")),
            headers: Depth1);
        var asUtc = await harness.SendAsync("REPORT", Calendar, CalDavTestHarness.Query(CalDavTestHarness.EventsIn("20261201T093000Z", "20261201T095900Z")),
            headers: Depth1);

        inVienna.Responses.Keys.Should().Equal(Href + "floating.ics");
        asUtc.Responses.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAlarmTimeRange_UsesTheTriggerTimesOfTheOccurrences() {
        await using var harness = CalDavTestHarness.Create();
        var reminded = CalDavTestData.Event("reminded", new DateTime(2026, 12, 1, 10, 0, 0), TimeSpan.FromHours(1), zone: TimeZoneInfo.Utc);
        reminded.Alarms.Add(new CalendarAlarm { Trigger = TimeSpan.FromMinutes(-30), Description = "Soon" });
        await harness.PublishAsync("shifts", "u-alice", reminded);

        var hit = await harness.SendAsync("REPORT", Calendar, CalDavTestHarness.Query(
            "<C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"VEVENT\"><C:comp-filter name=\"VALARM\">"
            + "<C:time-range start=\"20261201T092500Z\" end=\"20261201T093500Z\"/></C:comp-filter></C:comp-filter></C:comp-filter>"), headers: Depth1);
        var miss = await harness.SendAsync("REPORT", Calendar, CalDavTestHarness.Query(
            "<C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"VEVENT\"><C:comp-filter name=\"VALARM\">"
            + "<C:time-range start=\"20261201T100000Z\" end=\"20261201T110000Z\"/></C:comp-filter></C:comp-filter></C:comp-filter>"), headers: Depth1);

        hit.Responses.Keys.Should().Equal(Href + "reminded.ics");
        miss.Responses.Should().BeEmpty();
    }

    [Fact]
    public async Task Expand_AnswersEachInstanceInUtcWithoutRules() {
        await using var harness = await WithWeekAsync();
        var data = "<C:calendar-data><C:expand start=\"20261116T000000Z\" end=\"20261130T000000Z\"/></C:calendar-data>";

        var response = await harness.SendAsync("REPORT", Calendar,
            CalDavTestHarness.Query(CalDavTestHarness.EventsIn("20261116T000000Z", "20261130T000000Z"), data), headers: Depth1);

        var text = response.Prop(Href + "weekly.ics", DavNames.C.CalendarData)!.Text;
        text.Split("BEGIN:VEVENT").Should().HaveCount(3);
        text.Should().NotContain("RRULE").And.NotContain("VTIMEZONE");
        text.Should().Contain("DTSTART:20261116T080000Z").And.Contain("RECURRENCE-ID:20261123T080000Z");
    }

    [Fact]
    public async Task LimitRecurrenceSet_KeepsTheMasterAndOnlyOverridesInRange() {
        await using var harness = CalDavTestHarness.Create();
        var master = CalDavTestData.Event("series", new DateTime(2026, 12, 7, 9, 0, 0), TimeSpan.FromHours(1));
        master.RecurrenceRules.Add(new CalendarRecurrenceRule { Frequency = CalendarFrequency.Weekly, Count = 5 });
        var early = CalDavTestData.Event("series", new DateTime(2026, 12, 7, 11, 0, 0), TimeSpan.FromHours(1), "early");
        early.RecurrenceId = CalendarDateTime.Zoned(new DateTime(2026, 12, 7, 9, 0, 0), CalDavTestData.Vienna);
        var late = CalDavTestData.Event("series", new DateTime(2026, 12, 28, 11, 0, 0), TimeSpan.FromHours(1), "late");
        late.RecurrenceId = CalendarDateTime.Zoned(new DateTime(2026, 12, 28, 9, 0, 0), CalDavTestData.Vienna);
        await harness.PublishAsync("shifts", "u-alice", CalendarObject.FromItems(master, early, late));
        var body = $"<C:calendar-multiget {Ns}><D:prop><C:calendar-data><C:limit-recurrence-set start=\"20261220T000000Z\" end=\"20261231T000000Z\"/>"
                   + $"</C:calendar-data></D:prop><D:href>{Href}series.ics</D:href></C:calendar-multiget>";

        var text = (await harness.SendAsync("REPORT", Calendar, body)).Prop(Href + "series.ics", DavNames.C.CalendarData)!.Text;

        text.Should().Contain("SUMMARY:late").And.NotContain("SUMMARY:early").And.Contain("RRULE:");
    }

    [Fact]
    public async Task PartialCalendarData_KeepsOnlyTheNamedComponentsAndProperties() {
        await using var harness = await WithWeekAsync();
        var body = $"<C:calendar-multiget {Ns}><D:prop><C:calendar-data><C:comp name=\"VCALENDAR\"><C:prop name=\"VERSION\"/>"
                   + "<C:comp name=\"VEVENT\"><C:prop name=\"SUMMARY\"/><C:prop name=\"UID\"/></C:comp></C:comp></C:calendar-data></D:prop>"
                   + $"<D:href>{Href}this-week.ics</D:href></C:calendar-multiget>";

        var text = (await harness.SendAsync("REPORT", Calendar, body)).Prop(Href + "this-week.ics", DavNames.C.CalendarData)!.Text;

        text.Should().Contain("SUMMARY:Planning").And.Contain("UID:this-week").And.Contain("VERSION:2.0");
        text.Should().NotContain("DTSTART").And.NotContain("PRODID").And.NotContain("VTIMEZONE");
    }

    [Fact]
    public async Task CalDavTimezonesF_LeavesStandardTimeZonesOut() {
        await using var harness = await WithWeekAsync();
        var body = $"<C:calendar-multiget {Ns}><D:prop><C:calendar-data/></D:prop><D:href>{Href}this-week.ics</D:href></C:calendar-multiget>";

        var with = await harness.SendAsync("REPORT", Calendar, body);
        var without = await harness.SendAsync("REPORT", Calendar, body, headers: new Dictionary<string, string> { ["CalDAV-Timezones"] = "F" });

        with.Prop(Href + "this-week.ics", DavNames.C.CalendarData)!.Text.Should().Contain("BEGIN:VTIMEZONE");
        without.Prop(Href + "this-week.ics", DavNames.C.CalendarData)!.Text.Should().NotContain("BEGIN:VTIMEZONE").And.Contain("TZID=Europe/Vienna");
    }

    [Fact]
    public async Task FreeBusyQuery_AnswersTheMergedBusyTimeOfOpaqueEvents() {
        await using var harness = CalDavTestHarness.Create();
        var utc = TimeZoneInfo.Utc;
        await harness.PublishAsync("shifts", "u-alice", CalDavTestData.Event("a", new DateTime(2026, 12, 1, 9, 0, 0), TimeSpan.FromHours(2), zone: utc));
        await harness.PublishAsync("shifts", "u-alice", CalDavTestData.Event("b", new DateTime(2026, 12, 1, 10, 0, 0), TimeSpan.FromHours(2), zone: utc));
        var free = CalDavTestData.Event("c", new DateTime(2026, 12, 1, 14, 0, 0), TimeSpan.FromHours(1), zone: utc);
        free.Transparency = CalendarTransparency.Transparent;
        await harness.PublishAsync("shifts", "u-alice", free);
        var tentative = CalDavTestData.Event("d", new DateTime(2026, 12, 1, 16, 0, 0), TimeSpan.FromHours(1), zone: utc);
        tentative.Status = "TENTATIVE";
        await harness.PublishAsync("shifts", "u-alice", tentative);

        var response = await harness.SendAsync("REPORT", Calendar,
            $"<C:free-busy-query {Ns}><C:time-range start=\"20261201T000000Z\" end=\"20261202T000000Z\"/></C:free-busy-query>");

        response.Status.Should().Be(200);
        response.ContentType.Should().StartWith("text/calendar");
        response.Body.Should().Contain("FREEBUSY;FBTYPE=BUSY:20261201T090000Z/20261201T120000Z\r\n")
            .And.Contain("FREEBUSY;FBTYPE=BUSY-TENTATIVE:20261201T160000Z/20261201T170000Z").And.NotContain("T140000Z");
    }

    [Fact]
    public void TheEvaluator_ValidatesFilters() {
        CalDavFilterEvaluator.Validate(null).Should().NotBeNull();
        var valid = DavXml.Parse(System.Text.Encoding.UTF8.GetBytes($"<C:filter {Ns}>{CalDavTestHarness.EventsIn("20261116T000000Z", "20261123T000000Z")}</C:filter>"),
            new DavXmlLimits(4096, 16, 64)).Root;
        CalDavFilterEvaluator.Validate(valid).Should().BeNull();
        var badRange = DavXml.Parse(System.Text.Encoding.UTF8.GetBytes(
                $"<C:filter {Ns}><C:comp-filter name=\"VCALENDAR\"><C:time-range start=\"tomorrow\"/></C:comp-filter></C:filter>"),
            new DavXmlLimits(4096, 16, 64)).Root;
        CalDavFilterEvaluator.Validate(badRange).Should().Contain("time-range");
    }
}
