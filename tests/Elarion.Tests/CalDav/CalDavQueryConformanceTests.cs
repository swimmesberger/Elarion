using System.Security;
using System.Text;
using AwesomeAssertions;
using Elarion.CalDav;
using Elarion.Calendars;
using Elarion.Tests.Calendars;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>
/// One calendar of objects read from iCalendar text, and the request shapes the conformance tests send. The objects are
/// published as their components, so a test can hold any shape a producer writes (journals, free-busy, orphan overrides).
/// </summary>
internal static class CalDavConformance {
    public const string Path = "/calendars/alice/shifts.c/";
    public const string Href = "/dav/calendars/alice/shifts.c/";
    public const string Ns = CalDavTestHarness.Namespaces;
    public const string NotDefined = "<C:is-not-defined/>";

    public static readonly Dictionary<string, string> Depth1 = new() { ["Depth"] = "1" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A harness whose "shifts" source lists one calendar, <c>c</c>, in <paramref name="zone"/>, holding <paramref name="objects"/>.</summary>
    /// <param name="zone">The calendar's time zone (<c>calendar-timezone</c>); <see langword="null"/> for none, which is UTC.</param>
    /// <param name="objects">Each the components of one object (one UID), without <c>VCALENDAR</c>.</param>
    public static async Task<CalDavTestHarness> CalendarAsync(TimeZoneInfo? zone, params string[] objects) {
        var harness = CalDavTestHarness.Create();
        harness.Directory.Override = [
            new PublishedCalendarInfo("c", "Conformance") {
                TimeZone = zone,
                Components = CalendarComponentKinds.Event | CalendarComponentKinds.Todo | CalendarComponentKinds.Journal | CalendarComponentKinds.FreeBusy,
            },
        ];
        foreach (var text in objects) await PublishAsync(harness, text);
        return harness;
    }

    public static async Task PublishAsync(CalDavTestHarness harness, string components) {
        var read = await CalendarTestFiles.ReadAsync("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//Conformance//EN\n" + components.Trim() + "\nEND:VCALENDAR\n");
        await harness.PublishAsync("shifts", "c", CalendarObject.FromComponents(read.Root!));
    }

    /// <summary>The UIDs (object names without <c>.ics</c>) a calendar-query with <paramref name="filter"/> answers, sorted.</summary>
    public static async Task<string[]> UidsAsync(this CalDavTestHarness harness, string filter, string extra = "",
        IDictionary<string, string>? headers = null, string path = Path) {
        var response = await harness.SendAsync("REPORT", path, QueryBody(filter, extra), headers: headers ?? Depth1);
        response.Status.Should().Be(207, response.Body);
        return Uids(response);
    }

    public static string[] Uids(DavTestResponse response) =>
        [.. response.Responses.Keys.Select(static k => k[Href.Length..].Replace(".ics", "", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    public static string QueryBody(string filter, string extra = "", string prop = "<D:prop><D:getetag/></D:prop>") =>
        $"<C:calendar-query {Ns}>{prop}<C:filter>{filter}</C:filter>{extra}</C:calendar-query>";

    public static string Cal(string inner = "") => Comp("VCALENDAR", inner);

    public static string Events(string inner = "") => Cal(Comp("VEVENT", inner));

    public static string Todos(string inner = "") => Cal(Comp("VTODO", inner));

    public static string Comp(string name, string inner = "", string attributes = "") => $"<C:comp-filter name=\"{name}\"{attributes}>{inner}</C:comp-filter>";

    public static string Prop(string name, string inner = "") => $"<C:prop-filter name=\"{name}\">{inner}</C:prop-filter>";

    public static string Param(string name, string inner = "") => $"<C:param-filter name=\"{name}\">{inner}</C:param-filter>";

    public static string Text(string text, string? collation = null, bool negate = false, string attributes = "") =>
        $"<C:text-match{(collation is null ? "" : $" collation=\"{collation}\"")}{(negate ? " negate-condition=\"yes\"" : "")}{attributes}>{SecurityElement.Escape(text)}</C:text-match>";

    public static string Range(string? start, string? end) =>
        $"<C:time-range{(start is null ? "" : $" start=\"{start}\"")}{(end is null ? "" : $" end=\"{end}\"")}/>";

    /// <summary>A <c>CALDAV:timezone</c> element holding a <c>VCALENDAR</c> with the <c>VTIMEZONE</c> of <paramref name="zone"/>.</summary>
    public static async Task<string> TimezoneAsync(TimeZoneInfo zone) {
        var root = new CalendarComponent("VCALENDAR");
        root.Properties.Add(new CalendarProperty("VERSION", "2.0"));
        root.Properties.Add(CalendarProperty.Text("PRODID", "-//Test//Conformance//EN"));
        root.Components.Add(CalendarTimeZones.ToComponent(zone));
        using var stream = new MemoryStream();
        await CalendarWriter.WriteAsync(stream, root, cancellationToken: Ct);
        return "<C:timezone>" + SecurityElement.Escape(Encoding.UTF8.GetString(stream.ToArray())) + "</C:timezone>";
    }

    public static Task<DavTestResponse> FreeBusyAsync(this CalDavTestHarness harness, string? start, string? end, IDictionary<string, string>? headers = null,
        string path = Path) =>
        harness.SendAsync("REPORT", path, $"<C:free-busy-query {Ns}>{Range(start, end)}</C:free-busy-query>", headers: headers);

    public static TimeZoneInfo Zone(string id) => TimeZones.Get(id);
}

/// <summary>
/// calendar-query, free-busy-query and calendar-multiget edge cases ported from other servers' and clients' test suites
/// (spec 0006 FR-019, FR-022): Apple ccs-caldavtester, sabre/dav and sabre/vobject, Radicale, Xandikos, Cyrus
/// (Cassandane), python-caldav, caldav-server-tester, go-webdav, DAVx5 (dav4jvm) and vdirsyncer. Each test names its
/// source cases. Where the projects disagree, or a case conflicts with a documented Elarion choice, the test pins Elarion's
/// answer and says so.
/// </summary>
public sealed class CalDavQueryConformanceTests {
    private const string Href = CalDavConformance.Href;
    private const string NotDefined = CalDavConformance.NotDefined;

    // Two events (one a series whose override alone has DESCRIPTION and an alarm), a to-do and a journal.
    private const string FilterEvent = """
        BEGIN:VEVENT
        UID:ev1
        DTSTAMP:20260101T000000Z
        DTSTART:20260105T100000Z
        DTEND:20260105T110000Z
        SUMMARY:Event 1
        CATEGORIES:home
        CATEGORIES:work,errand
        ATTENDEE;PARTSTAT=DECLINED:mailto:a@example.com
        ATTENDEE;PARTSTAT=ACCEPTED:mailto:b@example.com
        BEGIN:VALARM
        ACTION:DISPLAY
        DESCRIPTION:Soon
        TRIGGER:-PT15M
        END:VALARM
        END:VEVENT
        """;

    private const string FilterSeries = """
        BEGIN:VEVENT
        UID:ev2
        DTSTAMP:20260101T000000Z
        DTSTART:20260106T090000Z
        DTEND:20260106T100000Z
        RRULE:FREQ=DAILY;COUNT=3
        SUMMARY:Event 20
        END:VEVENT
        BEGIN:VEVENT
        UID:ev2
        DTSTAMP:20260101T000000Z
        RECURRENCE-ID:20260107T090000Z
        DTSTART:20260107T120000Z
        DTEND:20260107T130000Z
        SUMMARY:Event 20 changed
        DESCRIPTION:Moved
        BEGIN:VALARM
        ACTION:DISPLAY
        DESCRIPTION:Soon
        TRIGGER:-PT15M
        END:VALARM
        END:VEVENT
        """;

    private const string FilterTodo = """
        BEGIN:VTODO
        UID:td1
        DTSTAMP:20260101T000000Z
        DUE:20260105T120000Z
        SUMMARY:Todo 1
        END:VTODO
        """;

    private const string FilterJournal = """
        BEGIN:VJOURNAL
        UID:jr1
        DTSTAMP:20260101T000000Z
        DTSTART;VALUE=DATE:20260105
        SUMMARY:Journal
        END:VJOURNAL
        """;

    private static Task<CalDavTestHarness> FilterCalendarAsync() =>
        CalDavConformance.CalendarAsync(null, FilterEvent, FilterSeries, FilterTodo, FilterJournal);

    private static string Cal(string inner = "") => CalDavConformance.Cal(inner);

    private static string Events(string inner = "") => CalDavConformance.Events(inner);

    private static string Todos(string inner = "") => CalDavConformance.Todos(inner);

    private static string Comp(string name, string inner = "", string attributes = "") => CalDavConformance.Comp(name, inner, attributes);

    private static string Prop(string name, string inner = "") => CalDavConformance.Prop(name, inner);

    private static string Param(string name, string inner = "") => CalDavConformance.Param(name, inner);

    private static string Text(string text, string? collation = null, bool negate = false, string attributes = "") =>
        CalDavConformance.Text(text, collation, negate, attributes);

    private static string Range(string? start, string? end) => CalDavConformance.Range(start, end);

    // ---- comp-filter, prop-filter, param-filter, text-match ----

    /// <summary>Apple Q-01, Q-02, Q-20; sabre Q01–Q03; Radicale Q02–Q04; go-webdav QY-01; caldav-server-tester QY-13; vdirsyncer QY-21.</summary>
    [Fact]
    public async Task CompFilters_SelectByComponentKind_AnEmptyKindAnswersAnEmpty207_AndIsNotDefinedInverts() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Events())).Should().Equal("ev1", "ev2");
        (await harness.UidsAsync(Todos())).Should().Equal("td1");
        (await harness.UidsAsync(Cal(Comp("VJOURNAL")))).Should().Equal("jr1");
        (await harness.UidsAsync(Cal(Comp("VFREEBUSY")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(NotDefined))).Should().Equal("jr1", "td1");
        (await harness.UidsAsync(Cal())).Should().Equal(["ev1", "ev2", "jr1", "td1"], "a VCALENDAR filter alone means every object");
    }

    /// <summary>sabre Q04, Q14; Apple Q-17: is-not-defined on a nested component holds when any component of the object lacks it.</summary>
    [Fact]
    public async Task NestedAlarmFilter_AndItsNegation_AreEvaluatedPerComponent() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Events(Comp("VALARM")))).Should().Equal("ev1", "ev2");
        (await harness.UidsAsync(Events(Comp("VALARM", NotDefined)))).Should().Equal(["ev2"], "the series' master has no alarm, only its override");
    }

    /// <summary>Apple Q-05, Q-06; sabre Q05: a property only in an override matches, and so does its absence through the master.</summary>
    [Fact]
    public async Task PropertyOnlyInAnOverride_MatchesBothThePropertyAndItsAbsence() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Events(Prop("DESCRIPTION")))).Should().Equal("ev2");
        (await harness.UidsAsync(Events(Prop("DESCRIPTION", NotDefined)))).Should().Equal("ev1", "ev2");
    }

    /// <summary>sabre Q11; go-webdav QY-05: iCalendar names are case-insensitive in a filter.</summary>
    [Fact]
    public async Task ComponentAndPropertyNames_AreCaseInsensitive() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Cal(Comp("vevent", Prop("description", Text("moved")))))).Should().Equal("ev2");
        (await harness.UidsAsync(Events(Comp("VALARM", Prop("action", Text("display")))))).Should().Equal("ev1", "ev2");
    }

    /// <summary>Xandikos Q12 (test_param_match_multiple_attendees); Radicale Q10, Q11: the text and the parameter must hold on one ATTENDEE.</summary>
    [Fact]
    public async Task TextMatchAndParamFilter_MustHoldOnTheSameAttendee() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Events(Prop("ATTENDEE", Text("a@example.com") + Param("PARTSTAT", Text("ACCEPTED")))))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Prop("ATTENDEE", Text("a@example.com") + Param("PARTSTAT", Text("DECLINED")))))).Should().Equal("ev1");
        (await harness.UidsAsync(Events(Prop("ATTENDEE", Param("PARTSTAT", Text("ACCEPTED", "i;octet")))))).Should().Equal("ev1");
        (await harness.UidsAsync(Events(Prop("ATTENDEE", Param("PARTSTAT", Text("accepted", "i;octet")))))).Should().BeEmpty();
    }

    /// <summary>Xandikos Q13; Apple Q-18, Q-19; Radicale Q08; python-caldav CT-16: each CATEGORIES line, and each of its values, is tested.</summary>
    [Fact]
    public async Task RepeatedAndMultiValuedCategories_AreEachTested() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Events(Prop("CATEGORIES", Text("errand"))))).Should().Equal("ev1");
        (await harness.UidsAsync(Events(Prop("CATEGORIES", Text("HOME"))))).Should().Equal(["ev1"], "the default collation is i;ascii-casemap");
        (await harness.UidsAsync(Events(Prop("CATEGORIES", Text("other"))))).Should().BeEmpty();
    }

    /// <summary>Radicale Q08; Apple Q-04, Q-08; python-caldav QY-09, QY-10: a negated text-match needs the property or parameter, and negates "contains".</summary>
    [Fact]
    public async Task NegatedTextMatch_OnAnAbsentPropertyOrParameter_IsFalse_AndNegatesASubstringTest() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Events(Prop("LOCATION", Text("x", negate: true))))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Prop("DTSTART", Param("TZID", Text("Paci", negate: true)))))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Prop("SUMMARY", Text("Event 2", negate: true))))).Should().Equal(["ev1"], "'Event 20' contains 'Event 2'");
    }

    /// <summary>Apple Q-09, Q-11, Q-15; Radicale Q07; sabre Q06: param-filters, prop-filters and sibling comp-filters are all required.</summary>
    [Fact]
    public async Task SeveralFiltersAtOneLevel_AreAllRequired() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Events(Prop("ATTENDEE", Param("PARTSTAT", Text("ACCEPTED")) + Param("PARTSTAT", Text("DECLINED")))))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Prop("SUMMARY", Text("Event")) + Prop("CATEGORIES", Text("home"))))).Should().Equal("ev1");
        (await harness.UidsAsync(Events(Prop("SUMMARY", Text("Event")) + Comp("VALARM")))).Should().Equal("ev1", "ev2");
        (await harness.UidsAsync(Cal(Comp("VEVENT") + Comp("VTODO")))).Should().BeEmpty();
    }

    public static TheoryData<string?, string, bool, bool> Collations => new() {
        { null, "MEETING", false, true },
        { null, "café", false, true },
        { null, "CAFÉ", false, false },
        { "i;ascii-casemap", "CAFÉ", true, true },
        { "i;unicode-casemap", "CAFÉ", false, true },
        { "i;unicode-casemap", "cafe", false, false },
        { "i;octet", "Café", false, true },
        { "i;octet", "café", false, false },
        { "i;octet", "café", true, true },
    };

    /// <summary>
    /// Xandikos Q14, Q15; sabre Q09, Q10, Q15–Q17; Radicale Q09; Apple Q-23: i;ascii-casemap folds only A–Z, i;unicode-casemap
    /// folds beyond ASCII, i;octet folds nothing, and negate-condition applies after the collation.
    /// </summary>
    [Theory]
    [MemberData(nameof(Collations))]
    public async Task Collations_FoldWhatTheirRfcSays(string? collation, string needle, bool negate, bool matches) {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:cafe
            DTSTART:20260105T100000Z
            SUMMARY:Café Meeting
            END:VEVENT
            """);

        var uids = await harness.UidsAsync(Events(Prop("SUMMARY", Text(needle, collation, negate))));

        uids.Should().Equal(matches ? new[] { "cafe" } : []);
    }

    /// <summary>sabre Q07, Q08, Q09: an explicitly written VALUE parameter is filtered like any other.</summary>
    [Fact]
    public async Task AnExplicitValueParameter_IsFilterable() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:day
            DTSTART;VALUE=DATE:20110704
            SUMMARY:Holiday
            END:VEVENT
            """);

        (await harness.UidsAsync(Events(Prop("DTSTART", Param("VALUE", Text("dAtE")))))).Should().Equal("day");
        (await harness.UidsAsync(Events(Prop("DTSTART", Param("VALUE", Text("dAtE", "i;octet")))))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Prop("DTSTART", Param("VALUE", NotDefined))))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Prop("DTSTART", Param("TZID", NotDefined))))).Should().Equal("day");
    }

    /// <summary>Xandikos Q17 (test_filter_with_empty_property): an empty value still defines the property.</summary>
    [Fact]
    public async Task APropertyWithAnEmptyValue_IsDefined() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:empty
            DTSTART:20260105T100000Z
            SUMMARY:
            END:VEVENT
            """);

        (await harness.UidsAsync(Events(Prop("SUMMARY")))).Should().Equal("empty");
        (await harness.UidsAsync(Events(Prop("SUMMARY", NotDefined)))).Should().BeEmpty();
    }

    /// <summary>
    /// Apple Q-21; Xandikos Q18. Pins Elarion's choice: RFC 4791 text-match knows only substring matching, so the CardDAV
    /// <c>match-type</c> attribute is ignored on a calendar-query (Apple and Xandikos honour it as an extension).
    /// </summary>
    [Fact]
    public async Task MatchTypeAttribute_IsIgnored_TextMatchIsAlwaysASubstringTest() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Events(Prop("SUMMARY", Text("Event 2", attributes: " match-type=\"equals\""))))).Should().Equal("ev2");
        (await harness.UidsAsync(Events(Prop("SUMMARY", Text("vent", attributes: " match-type=\"starts-with\""))))).Should().Equal("ev1", "ev2");
    }

    /// <summary>
    /// Apple Q-10, Q-12, Q-16. Pins Elarion's choice: the <c>test="anyof"</c> attribute of the calendar-query extensions is
    /// not part of RFC 4791 and is ignored, so the filters of one level stay all required.
    /// </summary>
    [Fact]
    public async Task TestAnyOfAttribute_IsIgnored_FiltersStayAllRequired() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Cal(Comp("VEVENT", Prop("SUMMARY", Text("Event 1")) + Prop("DESCRIPTION"), " test=\"anyof\"")))).Should().BeEmpty();
        (await harness.UidsAsync(Comp("VCALENDAR", Comp("VEVENT") + Comp("VTODO"), " test=\"anyof\""))).Should().BeEmpty();
    }

    public static TheoryData<string, string> InvalidFilters => new() {
        { "empty filter (Radicale Q01; RFC 4791 §9.7 needs the VCALENDAR comp-filter)", "<C:filter/>" },
        { "no filter (sabre Q85, Q96)", "" },
        { "two top-level comp-filters (sabre Q86)", "<C:filter><C:comp-filter name=\"VCALENDAR\"/><C:comp-filter name=\"VCALENDAR\"/></C:filter>" },
        { "end before start (sabre Q88)", "<C:filter>" + Events(Range("20150101T000000Z", "20140101T000000Z")) + "</C:filter>" },
        { "end before start on a property (sabre Q89)", "<C:filter>" + Events(Prop("DTSTART", Range("20150101T000000Z", "20140101T000000Z"))) + "</C:filter>" },
        { "empty time-range (Apple T-17)", "<C:filter>" + Events("<C:time-range/>") + "</C:filter>" },
        { "DATE without time (Apple T-21; python-caldav QY-19)", "<C:filter>" + Events(Range("20060102", "20060103")) + "</C:filter>" },
        // Pins Elarion's choice: a zero-width range (start = end) is refused; sabre Q40 accepts it and matches an instant.
        { "start equal to end (sabre Q40)", "<C:filter>" + Events(Range("20260105T100000Z", "20260105T100000Z")) + "</C:filter>" },
    };

    /// <summary>
    /// The malformed filters of Apple, sabre and Radicale. Pins Elarion's choice of 403 with CALDAV:valid-filter where sabre
    /// answers 400 (RFC 4791 §7.8 names the precondition).
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidFilters))]
    public async Task MalformedFilter_Answers403ValidFilter(string name, string filter) {
        _ = name;
        await using var harness = await FilterCalendarAsync();

        var response = await harness.SendAsync("REPORT", CalDavConformance.Path,
            $"<C:calendar-query {CalDavConformance.Ns}><D:prop><D:getetag/></D:prop>{filter}</C:calendar-query>", headers: CalDavConformance.Depth1);

        response.Status.Should().Be(403);
        response.Xml.Child(DavNames.C.ValidFilter).Should().NotBeNull();
    }

    /// <summary>
    /// Apple T-22; python-caldav QY-19. Pins Elarion's choice: a time-range value without the trailing Z is read as UTC
    /// rather than refused (Apple expects 403 valid-filter); a value without a time is refused (see the theory above).
    /// </summary>
    [Fact]
    public async Task TimeRangeWithoutTheUtcDesignator_IsReadAsUtc() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Events(Range("20260105T100000", "20260105T103000")))).Should().Equal("ev1");
    }

    /// <summary>RFC 4791 §7.5.1 (CALDAV:supported-collation); Xandikos Q16; sabre Q18: an unknown collation names its own precondition.</summary>
    [Fact]
    public async Task UnsupportedCollation_Answers403SupportedCollation() {
        await using var harness = await FilterCalendarAsync();

        var response = await harness.SendAsync("REPORT", CalDavConformance.Path,
            CalDavConformance.QueryBody(Events(Prop("SUMMARY", Text("x", "i;blah")))), headers: CalDavConformance.Depth1);

        response.Status.Should().Be(403);
        response.Xml.Child(new DavName(DavNames.CalDav, "supported-collation")).Should().NotBeNull(response.Body);
    }

    /// <summary>
    /// sabre Q87 and python-caldav QY-11 (SabreDAV refuses a time-range on VCALENDAR) versus Radicale Q35 and Xandikos Q36
    /// (they apply it to every child). RFC 4791 §9.9 defines time-range only for VEVENT, VTODO, VJOURNAL, VFREEBUSY and
    /// VALARM; matching nothing silently is neither answer, so the filter is expected to be refused as invalid.
    /// </summary>
    [Fact]
    public async Task TimeRangeOnTheCalendarComponent_IsRefusedAsAnInvalidFilter() {
        await using var harness = await FilterCalendarAsync();

        var response = await harness.SendAsync("REPORT", CalDavConformance.Path,
            CalDavConformance.QueryBody(Cal(Range("20260101T000000Z", "20260201T000000Z"))), headers: CalDavConformance.Depth1);

        response.Status.Should().Be(403, response.Body);
        response.Xml.Child(DavNames.C.ValidFilter).Should().NotBeNull();
    }

    /// <summary>python-caldav QY-12: a prop-filter directly under VCALENDAR tests the calendar's own properties (Xandikos, sabre).</summary>
    [Fact]
    public async Task PropFilterDirectlyUnderVCalendar_TestsTheCalendarsOwnProperties() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Cal(Prop("SUMMARY")))).Should().BeEmpty();
        (await harness.UidsAsync(Cal(Prop("PRODID")))).Should().Equal("ev1", "ev2", "jr1", "td1");
    }

    /// <summary>sabre Q62, Q63, Q64: a time-range on a property tests its value as an instant; on a text property it matches nothing (pinned: sabre throws).</summary>
    [Fact]
    public async Task PropertyTimeRange_TestsTheValue_AndOnATextPropertyMatchesNothing() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Events(Prop("DTSTART", Range("20260105T100000Z", "20260105T103000Z"))))).Should().Equal("ev1");
        (await harness.UidsAsync(Cal(Comp("VJOURNAL", Prop("DTSTART", Range("20260104T000000Z", "20260105T000000Z")))))).Should()
            .BeEmpty("the range ends where the date starts");
        (await harness.UidsAsync(Events(Prop("SUMMARY", Range("20260101T000000Z", "20270101T000000Z"))))).Should().BeEmpty();
    }

    /// <summary>sabre Q95; Radicale Q20; DAVx5 and vdirsyncer QY-20: data only when asked for, and a query without prop still lists hrefs.</summary>
    [Fact]
    public async Task QueryForGetETagOnly_ReturnsNoCalendarData_AndAQueryWithoutPropListsTheMatches() {
        await using var harness = await FilterCalendarAsync();

        var etagOnly = await harness.SendAsync("REPORT", CalDavConformance.Path, CalDavConformance.QueryBody(Todos()), headers: CalDavConformance.Depth1);
        var noProp = await harness.SendAsync("REPORT", CalDavConformance.Path, CalDavConformance.QueryBody(Todos(), prop: ""), headers: CalDavConformance.Depth1);

        etagOnly.Prop(Href + "td1.ics", DavNames.D.GetETag).Should().NotBeNull();
        etagOnly.Prop(Href + "td1.ics", DavNames.C.CalendarData).Should().BeNull();
        etagOnly.Body.Should().NotContain("BEGIN:VCALENDAR");
        noProp.Status.Should().Be(207);
        CalDavConformance.Uids(noProp).Should().Equal("td1");
    }

    /// <summary>
    /// Apple M-14 (members for every Depth) and sabre Q92 (400 for Depth 0) versus RFC 4791 §7.8. Pins Elarion's choice: a
    /// missing Depth asks about the members, Depth 0 only the collection, which is no calendar object, so the answer is empty.
    /// </summary>
    [Fact]
    public async Task QueryDepth_MissingMeansMembers_ZeroMeansTheCollectionAlone() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Todos(), headers: new Dictionary<string, string>())).Should().Equal("td1");
        (await harness.UidsAsync(Todos(), headers: new Dictionary<string, string> { ["Depth"] = "0" })).Should().BeEmpty();
    }

    /// <summary>RFC 4791 §7.8; Radicale Q21; sabre Q94; Apple M-16: a calendar-query sent to an object filters that object.</summary>
    [Fact]
    public async Task QuerySentToAnObject_FiltersThatObject() {
        await using var harness = await FilterCalendarAsync();

        var hit = await harness.SendAsync("REPORT", CalDavConformance.Path + "ev1.ics", CalDavConformance.QueryBody(Events()),
            headers: new Dictionary<string, string> { ["Depth"] = "0" });
        var miss = await harness.SendAsync("REPORT", CalDavConformance.Path + "ev1.ics", CalDavConformance.QueryBody(Todos()),
            headers: new Dictionary<string, string> { ["Depth"] = "0" });

        hit.Status.Should().Be(207, hit.Body);
        CalDavConformance.Uids(hit).Should().Equal("ev1");
        miss.Status.Should().Be(207);
        CalDavConformance.Uids(miss).Should().BeEmpty();
    }

    // ---- time-range on VEVENT ----

    /// <summary>
    /// Radicale Q33, Q37, Q39; Xandikos Q39–Q41; sabre Q30, Q44; Apple F-01, F-03; caldav-server-tester QY-15: the range is
    /// half-open at both ends, an event overlapping it matches even when it starts before it.
    /// </summary>
    [Fact]
    public async Task EventTimeRange_IsHalfOpen_AndMatchesByOverlap() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:hour
            DTSTART:20260302T100000Z
            DTEND:20260302T110000Z
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:allday
            DTSTART;VALUE=DATE:20260303
            DTEND;VALUE=DATE:20260304
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:twodays
            DTSTART:20260305T000000Z
            DURATION:P2D
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:zeroday
            DTSTART;VALUE=DATE:20261225
            DTEND;VALUE=DATE:20261225
            END:VEVENT
            """);

        (await harness.UidsAsync(Events(Range("20260302T110000Z", "20260302T120000Z")))).Should().BeEmpty("the event ends where the range starts");
        (await harness.UidsAsync(Events(Range("20260302T090000Z", "20260302T100000Z")))).Should().BeEmpty("the event starts where the range ends");
        (await harness.UidsAsync(Events(Range("20260302T100000Z", "20260302T100001Z")))).Should().Equal("hour");
        (await harness.UidsAsync(Events(Range("20260302T105959Z", "20260302T110000Z")))).Should().Equal("hour");
        (await harness.UidsAsync(Events(Range("20260304T000000Z", "20260304T010000Z")))).Should().BeEmpty("a DTEND date is exclusive");
        (await harness.UidsAsync(Events(Range("20260302T120000Z", "20260303T000000Z")))).Should().BeEmpty("the all-day event starts at the range's end");
        (await harness.UidsAsync(Events(Range("20260306T000000Z", "20260306T120000Z")))).Should().Equal(["twodays"], "the range lies inside the event");
        (await harness.UidsAsync(Events(Range("20261201T000000Z", "20270101T000000Z")))).Should().Equal(["zeroday"], "sabre Q32: a zero-length all-day event");
    }

    /// <summary>
    /// Apple F-01–F-04; sabre Q31, Q35; Xandikos Q56; python-caldav QY-18: floating times and dates are placed in the
    /// calendar's time zone, and a CALDAV:timezone in the request wins over it.
    /// </summary>
    [Fact]
    public async Task FloatingTimesAndDates_AreInTheCalendarZone_UnlessTheRequestNamesOne() {
        var berlin = CalDavConformance.Zone("Europe/Berlin");
        var newYork = CalDavConformance.Zone("America/New_York");
        await using var harness = await CalDavConformance.CalendarAsync(berlin, """
            BEGIN:VEVENT
            UID:allday
            DTSTART;VALUE=DATE:20261225
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:floating
            DTSTART:20261226T120000
            DTEND:20261226T130000
            END:VEVENT
            """);
        var inNewYork = await CalDavConformance.TimezoneAsync(newYork);

        (await harness.UidsAsync(Events(Range("20261224T230000Z", "20261225T000000Z")))).Should().Equal(["allday"], "the day starts at 23:00Z in Berlin");
        (await harness.UidsAsync(Events(Range("20261225T230000Z", "20261226T000000Z")))).Should().BeEmpty("and ends at 23:00Z");
        (await harness.UidsAsync(Events(Range("20261226T110000Z", "20261226T113000Z")))).Should().Equal("floating");
        (await harness.UidsAsync(Events(Range("20261226T120000Z", "20261226T123000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20261226T170000Z", "20261226T173000Z")), inNewYork)).Should().Equal("floating");
        (await harness.UidsAsync(Events(Range("20261226T040000Z", "20261226T050000Z")), inNewYork)).Should().Equal("allday");
    }

    /// <summary>Apple T-12: a CALDAV:timezone that holds no time zone answers 403 with CALDAV:valid-calendar-data.</summary>
    [Fact]
    public async Task RequestTimezoneWithoutAVTimezone_Answers403ValidCalendarData() {
        await using var harness = await FilterCalendarAsync();

        var response = await harness.SendAsync("REPORT", CalDavConformance.Path,
            CalDavConformance.QueryBody(Events(Range("20260101T000000Z", "20260201T000000Z")), "<C:timezone>BEGIN:VCALENDAR\nEND:VCALENDAR</C:timezone>"),
            headers: CalDavConformance.Depth1);

        response.Status.Should().Be(403);
        response.Xml.Child(new DavName(DavNames.CalDav, "valid-calendar-data")).Should().NotBeNull();
    }

    /// <summary>
    /// Apple T-13, E-09. Pins Elarion's choice: the server does not advertise <c>calendar-no-timezone</c>, so the RFC 7809
    /// CALDAV:timezone-id element is not read: floating times stay in the calendar's zone and an unknown id is no error.
    /// </summary>
    [Fact]
    public async Task RequestTimezoneId_IsNotRead() {
        await using var harness = await CalDavConformance.CalendarAsync(CalDavConformance.Zone("Europe/Berlin"), """
            BEGIN:VEVENT
            UID:floating
            DTSTART:20261226T120000
            DTEND:20261226T130000
            END:VEVENT
            """);

        (await harness.UidsAsync(Events(Range("20261226T110000Z", "20261226T113000Z")), "<C:timezone-id>America/New_York</C:timezone-id>"))
            .Should().Equal("floating");
        (await harness.UidsAsync(Events(Range("20261226T110000Z", "20261226T113000Z")), "<C:timezone-id>bogus</C:timezone-id>"))
            .Should().Equal("floating");
    }

    /// <summary>
    /// Xandikos Q49–Q51, Q54; Cyrus Q57; caldav-server-tester QY-17; sabre Q45: a TZID resolves by its IANA or Windows name
    /// without a VTIMEZONE, per instance across a DST change, and DTSTART and DTEND may use different zones.
    /// </summary>
    [Fact]
    public async Task ZonedTimes_ResolveByTheirTzid() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:tokyo
            DTSTART;TZID=Asia/Tokyo:20270120T120000
            DTEND;TZID=Asia/Tokyo:20270120T130000
            END:VEVENT
            """, """
            BEGIN:VTODO
            UID:tokyo-due
            DUE;TZID=Asia/Tokyo:20270121T120000
            END:VTODO
            """, """
            BEGIN:VEVENT
            UID:two-zones
            DTSTART;TZID=Europe/Berlin:20250127T130000
            DTEND;TZID=America/New_York:20250127T140000
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:windows
            DTSTART;TZID=W. Europe Standard Time:20250722T090500
            RRULE:FREQ=WEEKLY;BYDAY=TU;UNTIL=20270720T070500Z
            END:VEVENT
            """);

        (await harness.UidsAsync(Events(Range("20270120T031500Z", "20270120T034500Z")))).Should().Equal("tokyo");
        (await harness.UidsAsync(Events(Range("20270120T121500Z", "20270120T124500Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Prop("DTSTART", Range("20270120T030000Z", "20270120T033000Z"))))).Should().Equal("tokyo");
        (await harness.UidsAsync(Todos(Range("20270121T023000Z", "20270121T033000Z")))).Should().Equal("tokyo-due");
        (await harness.UidsAsync(Todos(Range("20270121T113000Z", "20270121T123000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20250127T140000Z", "20250128T000000Z")))).Should().Equal(["two-zones"], "it ends at 19:00Z");
        (await harness.UidsAsync(Events(Range("20260106T080000Z", "20260106T090000Z")))).Should().Equal(["windows"], "09:05 CET");
        (await harness.UidsAsync(Events(Range("20260106T090000Z", "20260106T100000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20260707T070000Z", "20260707T080000Z")))).Should().Equal(["windows"], "09:05 CEST");
    }

    /// <summary>
    /// Xandikos Q53 (an unknown TZID is read as floating) versus Elarion's reader, which leaves the item without a value.
    /// Pins Elarion's choice: such an event never matches a time-range, and the query still answers 207.
    /// </summary>
    [Fact]
    public async Task UnknownTzid_NeverMatchesATimeRange_AndTheQueryStillAnswers() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:bogus
            DTSTART;TZID=Some/Bogus:20250722T090500
            RRULE:FREQ=WEEKLY
            END:VEVENT
            """);

        (await harness.UidsAsync(Events(Range("20250722T080000Z", "20250722T100000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events())).Should().Equal("bogus");
    }

    // Radicale's event6–event9 (Europe/Paris).
    private static Task<CalDavTestHarness> RecurrenceCalendarAsync() => CalDavConformance.CalendarAsync(null, """
        BEGIN:VEVENT
        UID:event6
        DTSTART;TZID=Europe/Paris:20170601T080000
        DTEND;TZID=Europe/Paris:20170601T090000
        RRULE:FREQ=DAILY;UNTIL=20170602T060000Z
        END:VEVENT
        BEGIN:VEVENT
        UID:event6
        RECURRENCE-ID;TZID=Europe/Paris:20170602T080000
        DTSTART;TZID=Europe/Paris:20170701T080000
        DTEND;TZID=Europe/Paris:20170701T090000
        END:VEVENT
        """, """
        BEGIN:VEVENT
        UID:event7
        DTSTART;TZID=Europe/Paris:20170701T080000
        DTEND;TZID=Europe/Paris:20170701T090000
        RRULE:FREQ=DAILY
        END:VEVENT
        BEGIN:VEVENT
        UID:event7
        RECURRENCE-ID;TZID=Europe/Paris:20170702T080000
        DTSTART;TZID=Europe/Paris:20170702T080000
        DTEND;TZID=Europe/Paris:20170702T090000
        END:VEVENT
        BEGIN:VEVENT
        UID:event7
        RECURRENCE-ID;TZID=Europe/Paris:20170703T080000
        DTSTART;TZID=Europe/Paris:20170601T080000
        DTEND;TZID=Europe/Paris:20170601T090000
        END:VEVENT
        """, """
        BEGIN:VEVENT
        UID:event8
        DTSTART;TZID=Europe/Paris:20170601T080000
        DTEND;TZID=Europe/Paris:20170601T090000
        RDATE;TZID=Europe/Paris:20170701T080000
        END:VEVENT
        """, """
        BEGIN:VEVENT
        UID:event9
        DTSTART;VALUE=DATE-TIME;TZID=Europe/Paris:20170601T080000
        DTEND;VALUE=DATE-TIME:20170601T080000Z
        RRULE:FREQ=DAILY;UNTIL=20170602T060000Z
        END:VEVENT
        """);

    // Radicale's event2: an endless weekly series with one moved instance.
    private static Task<CalDavTestHarness> MovedSeriesAsync() => CalDavConformance.CalendarAsync(null, """
        BEGIN:VEVENT
        UID:event2
        DTSTART;TZID=Europe/Paris:20130902T180000
        DTEND;TZID=Europe/Paris:20130902T190000
        RRULE:FREQ=WEEKLY
        END:VEVENT
        BEGIN:VEVENT
        UID:event2
        RECURRENCE-ID;TZID=Europe/Paris:20130909T180000
        DTSTART;TZID=Europe/Paris:20130910T170000
        DTEND;TZID=Europe/Paris:20130910T180000
        END:VEVENT
        """);

    // Overrides without a master (RFC 4791 orphans), and an override of a series' first instance.
    private static Task<CalDavTestHarness> OrphanCalendarAsync() => CalDavConformance.CalendarAsync(null, """
        BEGIN:VEVENT
        UID:orphan
        RECURRENCE-ID:20130102T000000Z
        DTSTART:20130102T010000Z
        DTEND:20130102T020000Z
        END:VEVENT
        """, """
        BEGIN:VEVENT
        UID:orphans
        RECURRENCE-ID:20230909T160000Z
        DTSTART:20230101T160000Z
        DURATION:PT1H
        END:VEVENT
        BEGIN:VEVENT
        UID:orphans
        RECURRENCE-ID:20231231T160000Z
        DTSTART:20231231T160000Z
        DURATION:PT2H
        END:VEVENT
        """, """
        BEGIN:VEVENT
        UID:first-moved
        DTSTART:20140101T000000Z
        DTEND:20140101T010000Z
        RRULE:FREQ=DAILY;COUNT=3
        END:VEVENT
        BEGIN:VEVENT
        UID:first-moved
        RECURRENCE-ID:20140101T000000Z
        DTSTART:20140101T050000Z
        DTEND:20140101T060000Z
        END:VEVENT
        """);

    /// <summary>
    /// Radicale Q31–Q34 (test_time_range_filter_events, event6–event9): an override moved before its series' start or past
    /// its UNTIL, an RDATE without a rule, a DTEND in UTC for a zoned DTSTART, and both half-open boundaries.
    /// </summary>
    [Fact]
    public async Task Overrides_RDates_AndUntil_FollowRadicalesRecurrenceTable() {
        await using var harness = await RecurrenceCalendarAsync();

        (await harness.UidsAsync(Events(Range("20170601T063000Z", "20170601T070000Z")))).Should().Equal("event6", "event7", "event8", "event9");
        (await harness.UidsAsync(Events(Range("20170701T060000Z", null)))).Should().Equal("event6", "event7", "event8");
        (await harness.UidsAsync(Events(Range("20170702T070000Z", "20170704T060000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20170602T075959Z", "20170602T080000Z")))).Should().Equal("event9");
        (await harness.UidsAsync(Events(Range("20170602T080000Z", "20170603T083000Z")))).Should().BeEmpty();
    }

    /// <summary>Radicale Q26, Q38; Xandikos Q42; go-webdav QY-02: an override removes its regular instance and stands at its own time.</summary>
    [Fact]
    public async Task MovedOverride_RemovesTheRegularInstance_AndMatchesAtItsNewTime() {
        await using var harness = await MovedSeriesAsync();

        (await harness.UidsAsync(Events(Range("20130903T000000Z", "20130908T000000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20130909T150000Z", "20130909T170000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20130910T140000Z", "20130910T160000Z")))).Should().Equal("event2");
        (await harness.UidsAsync(Events(Range("20140801T000000Z", "20141001T000000Z")))).Should().Equal(["event2"], "an endless weekly series");
    }

    /// <summary>
    /// Apple T-14 (22.ics); Radicale Q43, Q44; Cyrus F09: an override without its master stands at its own DTSTART, two of
    /// them in one object each count (Radicale refuses the object; Elarion pins Cyrus's reading), and an override of the
    /// first instance replaces the master's own start.
    /// </summary>
    [Fact]
    public async Task OrphanOverrides_AndAnOverrideOfTheFirstInstance_StandAtTheirOwnStart() {
        await using var harness = await OrphanCalendarAsync();

        (await harness.UidsAsync(Events(Range("20130102T010000Z", "20130102T013000Z")))).Should().Equal("orphan");
        (await harness.UidsAsync(Events(Range("20130102T000000Z", "20130102T003000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20230101T160000Z", "20230101T170000Z")))).Should().Equal("orphans");
        (await harness.UidsAsync(Events(Range("20231231T170000Z", "20231231T173000Z")))).Should().Equal("orphans");
        (await harness.UidsAsync(Events(Range("20230909T160000Z", "20230909T170000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20140101T000000Z", "20140101T003000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20140101T050000Z", "20140101T053000Z")))).Should().Equal("first-moved");
    }

    /// <summary>Apple T-05–T-07: a time-range and a prop-filter in one comp-filter must hold on the same instance.</summary>
    [Fact]
    public async Task TimeRangeAndPropFilter_MustHoldOnTheSameInstance() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(Events(Range("20260106T090000Z", "20260106T100000Z") + Prop("SUMMARY", Text("changed"))))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20260107T120000Z", "20260107T130000Z") + Prop("SUMMARY", Text("changed"))))).Should().Equal("ev2");
        (await harness.UidsAsync(Events(Range("20260107T090000Z", "20260107T100000Z")))).Should().BeEmpty("the override replaced that instance");
        (await harness.UidsAsync(Events(Range("20260108T090000Z", "20260108T100000Z")))).Should().Equal("ev2");
    }

    /// <summary>
    /// Apple T-08, A-04, FB-01. Pins Elarion's documented choice (CalendarRecurrence): an override with
    /// RANGE=THISANDFUTURE replaces only its own instance; the later instances keep the master's time.
    /// </summary>
    [Fact]
    public async Task ThisAndFutureOverride_ReplacesOnlyItsOwnInstance() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:seven
            DTSTART;TZID=America/New_York:20270102T180000
            DURATION:PT1H
            RRULE:FREQ=DAILY;COUNT=5
            END:VEVENT
            BEGIN:VEVENT
            UID:seven
            RECURRENCE-ID;RANGE=THISANDFUTURE;TZID=America/New_York:20270104T180000
            DTSTART;TZID=America/New_York:20270104T200000
            DURATION:PT1H
            END:VEVENT
            """);

        (await harness.UidsAsync(Events(Range("20270105T010000Z", "20270105T020000Z")))).Should().Equal(["seven"], "the override of 01-04");
        (await harness.UidsAsync(Events(Range("20270105T230000Z", "20270106T000000Z")))).Should().Equal(["seven"], "01-05 stays at 18:00");
        (await harness.UidsAsync(Events(Range("20270106T010000Z", "20270106T020000Z")))).Should().BeEmpty();
    }

    /// <summary>sabre Q84 (testIssue220): a RECURRENCE-ID in UTC replaces the instance of a series written with a TZID.</summary>
    [Fact]
    public async Task RecurrenceIdInUtc_ReplacesTheInstanceOfAZonedSeries() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:berlin
            DTSTART;TZID=Europe/Berlin:20120601T180000
            DTEND;TZID=Europe/Berlin:20120601T190000
            RRULE:FREQ=DAILY;COUNT=10
            END:VEVENT
            BEGIN:VEVENT
            UID:berlin
            RECURRENCE-ID:20120606T160000Z
            DTSTART;TZID=Europe/Berlin:20120606T200000
            DTEND;TZID=Europe/Berlin:20120606T210000
            END:VEVENT
            """);

        (await harness.UidsAsync(Events(Range("20120606T160000Z", "20120606T163000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20120606T180000Z", "20120606T183000Z")))).Should().Equal("berlin");
    }

    /// <summary>
    /// sabre Q26, Q36, Q38, Q39; Radicale Q47; Apple T-14, F-09: an endless series far from its start, a series whose every
    /// instance is excluded (no match, no error), one RDATE with two values or two RDATEs, and a floating start with a UTC
    /// UNTIL.
    /// </summary>
    [Fact]
    public async Task RecurrenceSets_MatchByTheirInstances() {
        await using var endless = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:forever
            DTSTART:20060102T170000Z
            DURATION:PT1H
            RRULE:FREQ=DAILY
            END:VEVENT
            """);
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:all-excluded
            DTSTART:20130329T140000
            RRULE:FREQ=WEEKLY;BYDAY=FR;UNTIL=20130412T115959Z
            EXDATE:20130329T140000,20130405T140000
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:rdate-list
            DTSTART:20140901T000000Z
            DURATION:PT1H
            RDATE:20141001T000000Z,20141101T000000Z
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:rdate-lines
            DTSTART:20140901T000000Z
            DURATION:PT1H
            RDATE:20141001T000000Z
            RDATE:20141101T000000Z
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:floating-until
            DTSTART:20290902T090000
            DURATION:PT1H
            RRULE:FREQ=DAILY;UNTIL=20290905T235959Z
            END:VEVENT
            """);

        (await endless.UidsAsync(Events(Range("20261115T180000Z", "20261122T000000Z")))).Should().Equal("forever");
        (await endless.UidsAsync(Events(Range("20261115T180000Z", "20261116T170000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20130301T000000Z", "20130401T000000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20141001T000000Z", "20141002T000000Z")))).Should().Equal("rdate-lines", "rdate-list");
        (await harness.UidsAsync(Events(Range("20141101T000000Z", "20141102T000000Z")))).Should().Equal("rdate-lines", "rdate-list");
        (await harness.UidsAsync(Events(Range("20141201T000000Z", "20141202T000000Z")))).Should().BeEmpty();
        (await harness.UidsAsync(Events(Range("20290905T090000Z", "20290905T093000Z")))).Should().Equal("floating-until");
        (await harness.UidsAsync(Events(Range("20290906T090000Z", "20290906T093000Z")))).Should().BeEmpty();
    }

    // ---- time-range on VTODO (RFC 4791 §9.9) ----

    // sabre's to-dos of CalendarQueryValidatorTest (2011-01-01) and Radicale's todo5/todo10/todo11 (2013-09).
    private static Task<CalDavTestHarness> TodoCalendarAsync() => CalDavConformance.CalendarAsync(null,
        "BEGIN:VTODO\nUID:start-duration\nDTSTART:20110101T120000Z\nDURATION:PT1H\nEND:VTODO",
        "BEGIN:VTODO\nUID:start-due\nDTSTART:20110101T120000Z\nDUE:20110101T130000Z\nEND:VTODO",
        "BEGIN:VTODO\nUID:start-only\nDTSTART:20110101T120000Z\nEND:VTODO",
        "BEGIN:VTODO\nUID:due-only\nDUE:20110101T130000Z\nEND:VTODO",
        "BEGIN:VTODO\nUID:completed-created\nCOMPLETED:20110101T130000Z\nCREATED:20110101T110000Z\nEND:VTODO",
        "BEGIN:VTODO\nUID:completed-only\nCOMPLETED:20110101T130000Z\nEND:VTODO",
        "BEGIN:VTODO\nUID:created-only\nCREATED:20110101T110000Z\nEND:VTODO",
        "BEGIN:VTODO\nUID:no-dates\nSUMMARY:Someday\nEND:VTODO",
        "BEGIN:VTODO\nUID:todo5\nCREATED:20130903T160000Z\nCOMPLETED:20130920T160000Z\nEND:VTODO",
        "BEGIN:VTODO\nUID:todo10\nDTSTART:20130901T180000Z\nDUE:20130903T180000Z\nCREATED:20130901T170000Z\nCOMPLETED:20130901T173000Z\nEND:VTODO",
        "BEGIN:VTODO\nUID:todo11\nCREATED:20130920T160000Z\nCOMPLETED:20130903T160000Z\nEND:VTODO",
        "BEGIN:VTODO\nUID:due-date\nDUE;VALUE=DATE:20111225\nEND:VTODO",
        "BEGIN:VTODO\nUID:mixed\nDTSTART:20111223T120000Z\nDUE;VALUE=DATE:20111225\nEND:VTODO");

    public static TheoryData<string, string, string[]> TodoRanges => new() {
        // sabre Q46–Q53: f24, f25, f26.
        { "20110101T124500Z", "20110101T131500Z", ["completed-created", "completed-only", "created-only", "due-only", "no-dates", "start-due", "start-duration"] },
        { "20110201T100000Z", "20110201T130000Z", ["created-only", "no-dates"] },
        { "20110101T114500Z", "20110101T121500Z", ["completed-created", "created-only", "no-dates", "start-due", "start-duration", "start-only"] },
        // sabre Q54, Q55: DATE-valued DUE, alone and after a DATE-TIME DTSTART.
        { "20110101T000000Z", "20120101T000000Z", ["completed-created", "completed-only", "created-only", "due-date", "due-only", "mixed", "no-dates", "start-due", "start-duration", "start-only"] },
        { "20110101T000000Z", "20111101T000000Z", ["completed-created", "completed-only", "created-only", "due-only", "no-dates", "start-due", "start-duration", "start-only"] },
        // Radicale Q63: DTSTART and DUE take precedence over CREATED and COMPLETED (todo10).
        { "20130902T000000Z", "20130903T000000Z", ["created-only", "no-dates", "todo10"] },
        { "20130801T000000Z", "20130901T000000Z", ["created-only", "no-dates"] },
        // Radicale Q64: CREATED before COMPLETED (todo5) and after it (todo11), with <= and >= at both dates.
        { "20130903T140000Z", "20130903T150000Z", ["created-only", "no-dates", "todo10"] },
        { "20130903T150000Z", "20130903T160000Z", ["created-only", "no-dates", "todo10", "todo11", "todo5"] },
        { "20130920T160000Z", "20130920T170000Z", ["created-only", "no-dates", "todo11", "todo5"] },
        { "20130920T170000Z", "20130921T000000Z", ["created-only", "no-dates"] },
    };

    /// <summary>
    /// The VTODO table of RFC 4791 §9.9, every row: sabre Q46–Q56 (CalendarQueryValidatorTest, VTodoTest); Radicale Q59–Q64
    /// (todo5, todo10, todo11, with CREATED after COMPLETED); Apple T-09; Xandikos Q67.
    /// </summary>
    [Theory]
    [MemberData(nameof(TodoRanges))]
    public async Task TodoTimeRange_FollowsEveryRowOfTheRfc4791Table(string start, string end, string[] expected) {
        await using var harness = await TodoCalendarAsync();

        (await harness.UidsAsync(Todos(Range(start, end)))).Should().Equal(expected);
    }

    /// <summary>Radicale Q65: a time-range on the COMPLETED property.</summary>
    [Fact]
    public async Task TimeRangeOnCompleted_TestsTheCompletionTime() {
        await using var harness = await TodoCalendarAsync();

        (await harness.UidsAsync(Todos(Prop("COMPLETED", Range("20130918T000000Z", "20130922T000000Z"))))).Should().Equal("todo5");
    }

    /// <summary>
    /// Apple T-11 (106.ics) and Radicale Q66 (todo9) anchor a rule on DUE when DTSTART is missing; Xandikos treats such a
    /// rule as malformed. RFC 5545 §3.8.5.3 makes DTSTART the first instance. Pins Elarion's choice: the to-do counts once,
    /// at its DUE, and the rule adds nothing.
    /// </summary>
    [Fact]
    public async Task RuleOnATodoWithoutStart_AddsNoInstances() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VTODO
            UID:due-series
            DUE:20270102T160000Z
            RRULE:FREQ=DAILY;COUNT=5
            END:VTODO
            """);

        (await harness.UidsAsync(Todos(Range("20270102T000000Z", "20270103T000000Z")))).Should().Equal("due-series");
        (await harness.UidsAsync(Todos(Range("20270105T000000Z", "20270106T000000Z")))).Should().BeEmpty();
    }

    // ---- VJOURNAL and VFREEBUSY ----

    /// <summary>
    /// sabre Q57–Q60; Radicale Q68, Q69 (journal1–3): a journal without DTSTART never matches, a DATE-TIME one is an instant,
    /// a DATE one spans its day, and a recurring one matches by its instances.
    /// </summary>
    [Fact]
    public async Task JournalTimeRange_FollowsTheRfc4791Table() {
        await using var harness = await CalDavConformance.CalendarAsync(null,
            "BEGIN:VJOURNAL\nUID:journal1\nSUMMARY:No start\nEND:VJOURNAL",
            "BEGIN:VJOURNAL\nUID:journal2\nDTSTART;TZID=Europe/Paris:20000101T100000\nRRULE:FREQ=YEARLY\nEND:VJOURNAL",
            "BEGIN:VJOURNAL\nUID:journal3\nDTSTART;VALUE=DATE:20000101\nEND:VJOURNAL",
            "BEGIN:VJOURNAL\nUID:instant\nDTSTART:20110101T120000Z\nEND:VJOURNAL");
        string Journals(string? start, string? end) => Cal(Comp("VJOURNAL", Range(start, end)));

        (await harness.UidsAsync(Journals("19991229T000000Z", "20000202T000000Z"))).Should().Equal("journal2", "journal3");
        (await harness.UidsAsync(Journals("20000101T000000Z", "20000202T000000Z"))).Should().Equal(["journal2", "journal3"], "a date at the range's start");
        (await harness.UidsAsync(Journals("19981229T000000Z", "19991012T000000Z"))).Should().BeEmpty();
        (await harness.UidsAsync(Journals("20110101T124500Z", "20110101T131500Z"))).Should().BeEmpty();
        (await harness.UidsAsync(Journals("20110101T114500Z", "20110101T121500Z"))).Should().Equal("instant");
        (await harness.UidsAsync(Journals("20131229T000000Z", "21520202T000000Z"))).Should().Equal(["journal2"], "the yearly journal recurs");
        (await harness.UidsAsync(Journals("20051229T000000Z", "20060202T000000Z"))).Should().Equal("journal2");
    }

    /// <summary>sabre Q61 (an exception in sabre): RFC 4791 §9.9 defines a VFREEBUSY's overlap by its FREEBUSY periods, else DTSTART and DTEND.</summary>
    [Fact]
    public async Task FreeBusyComponentTimeRange_UsesItsPeriods() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VFREEBUSY
            UID:fb
            DTSTART:20270108T000000Z
            DTEND:20270109T000000Z
            FREEBUSY:20270108T130000Z/20270108T150000Z,20270108T160000Z/PT1H
            END:VFREEBUSY
            """, """
            BEGIN:VFREEBUSY
            UID:fb-span
            DTSTART:20270110T000000Z
            DTEND:20270111T000000Z
            END:VFREEBUSY
            """);
        string FreeBusy(string start, string end) => Cal(Comp("VFREEBUSY", Range(start, end)));

        (await harness.UidsAsync(FreeBusy("20270108T140000Z", "20270108T143000Z"))).Should().Equal("fb");
        (await harness.UidsAsync(FreeBusy("20270108T163000Z", "20270108T170000Z"))).Should().Equal("fb");
        (await harness.UidsAsync(FreeBusy("20270108T150000Z", "20270108T160000Z"))).Should().BeEmpty("between the periods");
        (await harness.UidsAsync(FreeBusy("20270110T120000Z", "20270110T130000Z"))).Should().Equal("fb-span");
    }

    // ---- time-range on VALARM ----

    // sabre CalendarQueryValidatorTest: events at 2011-01-01T12:00Z; f31 = [10:45, 11:15), f32 = [11:45, 12:15).
    private const string F31Start = "20110101T104500Z";
    private const string F31End = "20110101T111500Z";
    private const string F32Start = "20110101T114500Z";
    private const string F32End = "20110101T121500Z";

    private static string Alarm(string trigger, string extra = "") => $"BEGIN:VALARM\nACTION:DISPLAY\nDESCRIPTION:Alarm\n{trigger}\n{extra}END:VALARM\n";

    private static string AlarmsIn(string? start, string? end) => Events(Comp("VALARM", Range(start, end)));

    /// <summary>
    /// sabre Q67–Q70, Q72, Q73; Radicale Q71; Xandikos Q72–Q75; Apple A-02, A-05: the alarm's own time is matched, RELATED=END
    /// uses DTEND, DURATION, the start when there is no end, or a to-do's DUE; REPEAT adds repetitions, DURATION alone does not.
    /// </summary>
    [Fact]
    public async Task AlarmTimeRange_MatchesTheTriggerTimes() {
        await using var harness = await CalDavConformance.CalendarAsync(null,
            "BEGIN:VEVENT\nUID:end-no-end\nDTSTART:20110101T120000Z\n" + Alarm("TRIGGER;RELATED=END:-PT1H") + "END:VEVENT",
            "BEGIN:VEVENT\nUID:end-dtend\nDTSTART:20110101T120000Z\nDTEND:20110101T130000Z\n" + Alarm("TRIGGER;RELATED=END:-PT2H") + "END:VEVENT",
            "BEGIN:VEVENT\nUID:end-duration\nDTSTART:20110101T120000Z\nDURATION:PT1H\n" + Alarm("TRIGGER;RELATED=END:-PT2H") + "END:VEVENT",
            "BEGIN:VEVENT\nUID:absolute\nDTSTART:20110101T120000Z\n" + Alarm("TRIGGER;VALUE=DATE-TIME:20110101T110000Z") + "END:VEVENT",
            "BEGIN:VEVENT\nUID:repeat\nDTSTART:20110101T120000Z\n" + Alarm("TRIGGER;VALUE=DATE-TIME:20110101T090000Z", "REPEAT:2\nDURATION:PT1H\n") + "END:VEVENT",
            "BEGIN:VEVENT\nUID:no-repeat\nDTSTART:20110101T120000Z\n" + Alarm("TRIGGER;VALUE=DATE-TIME:20110101T090000Z", "DURATION:PT1H\n") + "END:VEVENT",
            "BEGIN:VEVENT\nUID:zero\nDTSTART:20110101T120000Z\nDTEND:20110101T130000Z\n" + Alarm("TRIGGER:PT0S") + "END:VEVENT",
            "BEGIN:VEVENT\nUID:no-trigger\nDTSTART:20110101T120000Z\nBEGIN:VALARM\nACTION:DISPLAY\nDESCRIPTION:x\nEND:VALARM\nEND:VEVENT",
            "BEGIN:VTODO\nUID:todo-end\nDTSTART:20110101T120000Z\nDUE:20110101T130000Z\n" + Alarm("TRIGGER;RELATED=END:-PT2H") + "END:VTODO");

        (await harness.UidsAsync(AlarmsIn(F31Start, F31End))).Should().Equal("absolute", "end-dtend", "end-duration", "end-no-end", "repeat");
        (await harness.UidsAsync(AlarmsIn(F32Start, F32End))).Should().Equal("zero");
        (await harness.UidsAsync(AlarmsIn("20110101T084500Z", "20110101T091500Z"))).Should().Equal("no-repeat", "repeat");
        (await harness.UidsAsync(AlarmsIn("20110101T121500Z", "20110101T130000Z"))).Should().BeEmpty("the events run, but no alarm rings");
        (await harness.UidsAsync(Todos(Comp("VALARM", Range(F31Start, F31End))))).Should().Equal("todo-end");
    }

    /// <summary>
    /// sabre Q75–Q77 (ValarmTest testValarm, testAlarmWayBefore); Radicale Q70: an alarm far before its event, or before an
    /// instance after the range, is found; one of a series with a single instance is not.
    /// </summary>
    [Fact]
    public async Task AlarmFarFromItsEvent_IsFound() {
        await using var harness = await CalDavConformance.CalendarAsync(null,
            "BEGIN:VEVENT\nUID:monthly\nDTSTART:20120101T120000Z\nRRULE:FREQ=MONTHLY\n" + Alarm("TRIGGER:-P15D") + "END:VEVENT",
            "BEGIN:VEVENT\nUID:monthly-once\nDTSTART:20120101T120000Z\nRRULE:FREQ=MONTHLY;COUNT=1\n" + Alarm("TRIGGER:-P15D") + "END:VEVENT",
            "BEGIN:VEVENT\nUID:way-before\nDTSTART:20120101T120000Z\n" + Alarm("TRIGGER:-P2W1D") + "END:VEVENT",
            "BEGIN:VEVENT\nUID:year-long\nDTSTART:20151010T060000Z\nDTEND:20161010T070000Z\n" + Alarm("TRIGGER:-PT15M") + "END:VEVENT");

        (await harness.UidsAsync(AlarmsIn("20120510T000000Z", "20120520T000000Z"))).Should().Equal(["monthly"], "June 1 alerts on May 17");
        (await harness.UidsAsync(AlarmsIn("20111210T000000Z", "20111220T000000Z"))).Should().Equal("monthly", "monthly-once", "way-before");
        (await harness.UidsAsync(AlarmsIn("20151010T053000Z", "20151010T055000Z"))).Should().Equal("year-long");
        (await harness.UidsAsync(AlarmsIn("20151010T080000Z", "20151010T090000Z"))).Should().BeEmpty();
    }

    /// <summary>Radicale Q70 (valarm2) and sabre Q69: an absolute trigger is matched at its own instant, even two weeks before its event.</summary>
    [Fact]
    public async Task AbsoluteTriggerFarBeforeItsEvent_IsFound() {
        await using var harness = await CalDavConformance.CalendarAsync(null,
            "BEGIN:VEVENT\nUID:absolute-early\nDTSTART:20120101T120000Z\n" + Alarm("TRIGGER;VALUE=DATE-TIME:20111217T120000Z") + "END:VEVENT");

        (await harness.UidsAsync(AlarmsIn("20111210T000000Z", "20111220T000000Z"))).Should().Equal("absolute-early");
    }

    /// <summary>Xandikos Q75 (test_no_trigger): an alarm without TRIGGER matches nothing, and does not hide the next alarm of the event.</summary>
    [Fact]
    public async Task AlarmWithoutTrigger_DoesNotHideTheNextAlarm() {
        await using var harness = await CalDavConformance.CalendarAsync(null,
            "BEGIN:VEVENT\nUID:two-alarms\nDTSTART:20110101T120000Z\nBEGIN:VALARM\nACTION:DISPLAY\nDESCRIPTION:x\nEND:VALARM\n"
            + Alarm("TRIGGER:-PT1H") + "END:VEVENT");

        (await harness.UidsAsync(AlarmsIn(F31Start, F31End))).Should().Equal("two-alarms");
    }

    /// <summary>Apple A-03, A-04: an alarm only in an override rings for that instance alone.</summary>
    [Fact]
    public async Task AlarmOnlyInAnOverride_RingsForThatInstanceAlone() {
        await using var harness = await FilterCalendarAsync();

        (await harness.UidsAsync(AlarmsIn("20260107T113000Z", "20260107T120000Z"))).Should().Equal("ev2");
        (await harness.UidsAsync(AlarmsIn("20260108T083000Z", "20260108T090000Z"))).Should().BeEmpty();
    }

    /// <summary>
    /// sabre Q71, Q74, Q82, Q83 (testIssue205, testIssue211): far trigger offsets, a zero-length trigger, a DATE-TIME EXDATE
    /// on an all-day series and an alarm inside a journal all answer quickly and match nothing that does not ring.
    /// </summary>
    [Fact]
    public async Task AwkwardAlarms_TerminateAndMatchOnlyWhatRings() {
        await using var harness = await CalDavConformance.CalendarAsync(null,
            "BEGIN:VEVENT\nUID:p52w\nDTSTART;VALUE=DATE:20120628\nRRULE:FREQ=DAILY\n" + Alarm("TRIGGER:P52W") + "END:VEVENT",
            "BEGIN:VEVENT\nUID:issue211\nDTSTART;VALUE=DATE:20100330\nRRULE:FREQ=YEARLY\nEXDATE;TZID=Europe/Berlin:20120330T000000\n"
            + Alarm("TRIGGER;VALUE=DATE-TIME:20110330T080000Z") + "END:VEVENT",
            "BEGIN:VJOURNAL\nUID:journal-alarm\nDTSTART:20120915T120000Z\n" + Alarm("TRIGGER:-PT1H") + "END:VJOURNAL");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        (await harness.UidsAsync(AlarmsIn("20120901T000000Z", "20121001T000000Z"))).Should().BeEmpty();
        (await harness.UidsAsync(AlarmsIn("20120426T000000Z", "20120427T000000Z"))).Should().BeEmpty();
        (await harness.UidsAsync(Cal(Comp("VJOURNAL", Comp("VALARM", Range("20120915T000000Z", "20120916T000000Z")))))).Should().BeEmpty();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    // ---- free-busy-query ----

    private static string Busy(string uid, string start, string end, string extra = "") =>
        $"BEGIN:VEVENT\nUID:{uid}\nDTSTART:{start}\nDTEND:{end}\n{extra}END:VEVENT";

    /// <summary>
    /// Apple FB-01, FB-02; sabre F04–F10; Radicale F01, F03; Xandikos F11: overlapping and back-to-back periods merge,
    /// periods are clipped to the range, cancelled, transparent and zero-length events are free, tentative ones are
    /// BUSY-TENTATIVE (also in lower case), and the answer is a plain VFREEBUSY without the events' details.
    /// </summary>
    [Fact]
    public async Task FreeBusy_MergesClipsAndClassifiesThePeriods() {
        await using var harness = await CalDavConformance.CalendarAsync(null,
            Busy("overlap-a", "20270107T150000Z", "20270107T160000Z", "SUMMARY:Secret\nLOCATION:Room\n"),
            Busy("overlap-b", "20270107T153000Z", "20270107T163000Z"),
            Busy("back-a", "20270108T150000Z", "20270108T160000Z"),
            Busy("back-b", "20270108T160000Z", "20270108T170000Z"),
            Busy("tentative", "20270108T180000Z", "20270108T190000Z", "STATUS:tentative\n"),
            Busy("cancelled", "20270108T200000Z", "20270108T210000Z", "STATUS:CANCELLED\n"),
            Busy("transparent", "20270108T210000Z", "20270108T220000Z", "TRANSP:TRANSPARENT\n"),
            "BEGIN:VEVENT\nUID:instant\nDTSTART:20270108T230000Z\nEND:VEVENT",
            Busy("before", "20270106T220000Z", "20270107T020000Z", "TRANSP:opaque\n"),
            Busy("after", "20270108T235000Z", "20270109T030000Z"));

        var response = await harness.FreeBusyAsync("20270107T000000Z", "20270109T000000Z");

        response.Status.Should().Be(200);
        response.ContentType.Should().StartWith("text/calendar");
        var body = response.Body.Replace("\r\n ", "", StringComparison.Ordinal);
        body.Should().Contain("20270107T000000Z/20270107T020000Z", "clipped at the range's start")
            .And.Contain("20270107T150000Z/20270107T163000Z")
            .And.Contain("20270108T150000Z/20270108T170000Z")
            .And.Contain("20270108T235000Z/20270109T000000Z", "clipped at the range's end")
            .And.Contain("FREEBUSY;FBTYPE=BUSY-TENTATIVE:20270108T180000Z/20270108T190000Z");
        body.Should().NotContain("T200000Z/").And.NotContain("T210000Z/").And.NotContain("20270108T230000Z/");
        body.Should().NotContain("METHOD:").And.NotContain("Secret").And.NotContain("Room").And.NotContain("UID:overlap");
    }

    /// <summary>
    /// sabre F01, F11–F14; Apple F-06, F-07; Cyrus F06: floating times and all-day events are placed in the calendar's zone,
    /// then clipped to the range.
    /// </summary>
    [Fact]
    public async Task FreeBusy_PlacesFloatingTimesAndDatesInTheCalendarZone() {
        await using var harness = await CalDavConformance.CalendarAsync(CalDavConformance.Zone("America/Toronto"),
            "BEGIN:VEVENT\nUID:allday\nDTSTART;VALUE=DATE:20110101\nEND:VEVENT",
            "BEGIN:VEVENT\nUID:floating\nDTSTART:20110102T120000\nDTEND:20110102T130000\nEND:VEVENT");

        var body = (await harness.FreeBusyAsync("20110101T110000Z", "20110103T110000Z")).Body.Replace("\r\n ", "", StringComparison.Ordinal);

        body.Should().Contain("20110101T110000Z/20110102T050000Z").And.Contain("20110102T170000Z/20110102T180000Z");
    }

    /// <summary>
    /// Radicale F03; sabre F15; Cyrus F09, F10: per-instance STATUS and TRANSP of overrides, an EXDATE, a series whose only
    /// instance is excluded (no error) and overrides without a master (busy at their own DTSTART).
    /// </summary>
    [Fact]
    public async Task FreeBusy_FollowsEachInstance() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VEVENT
            UID:series
            DTSTART:20130902T100000Z
            DTEND:20130902T110000Z
            RRULE:FREQ=DAILY;COUNT=5
            EXDATE:20130906T100000Z
            END:VEVENT
            BEGIN:VEVENT
            UID:series
            RECURRENCE-ID:20130903T100000Z
            DTSTART:20130903T100000Z
            DTEND:20130903T110000Z
            STATUS:CANCELLED
            END:VEVENT
            BEGIN:VEVENT
            UID:series
            RECURRENCE-ID:20130904T100000Z
            DTSTART:20130904T150000Z
            DTEND:20130904T160000Z
            END:VEVENT
            BEGIN:VEVENT
            UID:series
            RECURRENCE-ID:20130905T100000Z
            DTSTART:20130905T100000Z
            DTEND:20130905T110000Z
            TRANSP:TRANSPARENT
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:gone
            DTSTART:20130902T200000Z
            DTEND:20130902T210000Z
            RRULE:FREQ=DAILY;COUNT=1
            EXDATE:20130902T200000Z
            END:VEVENT
            """, """
            BEGIN:VEVENT
            UID:orphans
            RECURRENCE-ID:20130909T160000Z
            DTSTART:20130907T160000Z
            DURATION:PT1H
            END:VEVENT
            """);

        var response = await harness.FreeBusyAsync("20130901T000000Z", "20130908T000000Z");
        var body = response.Body.Replace("\r\n ", "", StringComparison.Ordinal);

        response.Status.Should().Be(200);
        body.Should().Contain("20130902T100000Z/20130902T110000Z").And.Contain("20130904T150000Z/20130904T160000Z")
            .And.Contain("20130907T160000Z/20130907T170000Z");
        body.Should().NotContain("20130903T100000Z/").And.NotContain("20130904T100000Z/").And.NotContain("20130905T100000Z/")
            .And.NotContain("20130906T100000Z/").And.NotContain("20130902T200000Z/");
    }

    public static TheoryData<string> FreeBusyWithoutRange => new() {
        $"<C:free-busy-query {CalDavConformance.Ns}/>",
        $"<C:free-busy-query {CalDavConformance.Ns}><C:time-range/></C:free-busy-query>",
        $"<C:free-busy-query {CalDavConformance.Ns}><C:time-range start=\"19900101\" end=\"20400101\"/></C:free-busy-query>",
    };

    /// <summary>sabre F02; Cyrus F07; Apple FB-03: a free-busy-query without a usable time-range answers 400.</summary>
    [Theory]
    [MemberData(nameof(FreeBusyWithoutRange))]
    public async Task FreeBusyWithoutAUsableRange_Answers400(string body) {
        await using var harness = await FilterCalendarAsync();

        (await harness.SendAsync("REPORT", CalDavConformance.Path, body)).Status.Should().Be(400);
    }

    /// <summary>Cyrus F07 (start only answers 200); Apple FB-04 (every Depth answers the same busy time).</summary>
    [Fact]
    public async Task FreeBusy_WithAnOpenEnd_AndAnyDepth_AnswersTheBusyTime() {
        await using var harness = await CalDavConformance.CalendarAsync(null, Busy("a", "20150101T120000Z", "20150101T130000Z"));

        var open = await harness.FreeBusyAsync("19700101T000000Z", null);
        var depth0 = await harness.FreeBusyAsync("20150101T000000Z", "20150102T000000Z", new Dictionary<string, string> { ["Depth"] = "0" });
        var depth1 = await harness.FreeBusyAsync("20150101T000000Z", "20150102T000000Z", CalDavConformance.Depth1);

        open.Status.Should().Be(200);
        open.Body.Should().Contain("20150101T120000Z/20150101T130000Z");
        depth0.Body.Should().Contain("20150101T120000Z/20150101T130000Z");
        depth1.Body.Should().Contain("20150101T120000Z/20150101T130000Z");
    }

    /// <summary>
    /// Apple FB-02 (Apple also accepts the variant without VFREEBUSY input); sabre F16. Pins Elarion's choice: the busy time
    /// is computed from events; a VFREEBUSY component stored in the calendar is not folded into the answer.
    /// </summary>
    [Fact]
    public async Task FreeBusy_DoesNotFoldStoredFreeBusyComponents() {
        await using var harness = await CalDavConformance.CalendarAsync(null, """
            BEGIN:VFREEBUSY
            UID:fb
            DTSTART:20270108T000000Z
            DTEND:20270109T000000Z
            FREEBUSY;FBTYPE=BUSY-UNAVAILABLE:20270108T130000Z/20270108T150000Z
            END:VFREEBUSY
            """);

        var response = await harness.FreeBusyAsync("20270108T000000Z", "20270109T000000Z");

        response.Status.Should().Be(200);
        response.Body.Should().NotContain("20270108T130000Z/");
    }

    /// <summary>Radicale F02; Apple FB-07: a free-busy-query sent to an object is refused with a 4xx.</summary>
    [Fact]
    public async Task FreeBusyOnAnObject_IsRefused() {
        await using var harness = await FilterCalendarAsync();

        var response = await harness.FreeBusyAsync("20260101T000000Z", "20260201T000000Z", path: CalDavConformance.Path + "ev1.ics");

        response.Status.Should().BeInRange(400, 499);
    }

    // ---- calendar-multiget ----

    private static string Multiget(params string[] hrefs) =>
        $"<C:calendar-multiget {CalDavConformance.Ns}><D:prop><D:getetag/></D:prop>{string.Concat(hrefs.Select(static h => $"<D:href>{h}</D:href>"))}</C:calendar-multiget>";

    /// <summary>
    /// vdirsyncer MG-11 (a duplicate href is answered), python-caldav MG-06 and DAVx5 MG-07 (a percent-encoded href resolves),
    /// Apple M-09 (decoded once: a double-encoded href is another name). Pins Elarion's choice: a found object answers under
    /// its canonical, percent-encoded href (the spelling the server lists, which DAVx5 echoes); a missing one under the href
    /// as sent.
    /// </summary>
    [Fact]
    public async Task MultigetHrefs_AreDecodedOnce_EchoedAsSent_AndDuplicatesAnswered() {
        await using var harness = await CalDavConformance.CalendarAsync(null, "BEGIN:VEVENT\nUID:a@b\nDTSTART:20260105T100000Z\nEND:VEVENT");

        var response = await harness.SendAsync("REPORT", CalDavConformance.Path,
            Multiget(Href + "a%40b.ics", Href + "a@b.ics", Href + "a@b.ics", Href + "a%2540b.ics"));

        response.Status.Should().Be(207);
        var answers = response.Xml.ChildrenNamed(DavNames.D.Response)
            .Select(static r => (Href: r.Child(DavNames.D.Href)!.Text.Trim(), Found: r.Child(DavNames.D.Status) is null)).ToList();
        answers.Should().Equal((Href + "a%40b.ics", true), (Href + "a%40b.ics", true), (Href + "a%40b.ics", true), (Href + "a%2540b.ics", false));
    }

    /// <summary>
    /// Cyrus M01 (garbage, ancestors and the collection itself each get an answer); Apple M-07, M-11; python-caldav MG-01:
    /// every href is answered, one that names no object of this calendar with its own 404.
    /// </summary>
    [Fact]
    public async Task MultigetHrefsThatNameNoObject_EachAnswer404() {
        await using var harness = await FilterCalendarAsync();
        string[] hrefs = ["nonsense", "/dav/calendars/", "/dav/calendars/alice/", Href, Href + "missing.ics", "/dav/calendars/alice/teams.red/ev1.ics", Href + "ev1.ics"];

        var response = await harness.SendAsync("REPORT", CalDavConformance.Path, Multiget(hrefs));

        response.Status.Should().Be(207);
        var answers = response.Xml.ChildrenNamed(DavNames.D.Response).ToList();
        answers.Select(static r => r.Child(DavNames.D.Href)!.Text.Trim()).Should().Equal(hrefs);
        answers.SkipLast(1).Should().OnlyContain(static r => r.Child(DavNames.D.Status) != null && r.Child(DavNames.D.Status)!.Text.Contains("404"));
        response.Prop(Href + "ev1.ics", DavNames.D.GetETag).Should().NotBeNull();
    }

    /// <summary>
    /// vdirsyncer MG-12 asks for an empty 207. Pins Elarion's choice: RFC 4791 §9.10 requires at least one href, so an empty
    /// multiget answers 400.
    /// </summary>
    [Fact]
    public async Task MultigetWithoutHrefs_Answers400() {
        await using var harness = await FilterCalendarAsync();

        (await harness.SendAsync("REPORT", CalDavConformance.Path, Multiget())).Status.Should().Be(400);
    }

    /// <summary>Xandikos M05; vdirsyncer MG-10: a multiget answers the same with Depth missing, 0 or 1.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("1")]
    public async Task Multiget_IgnoresTheDepth(string? depth) {
        await using var harness = await FilterCalendarAsync();
        var headers = depth is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Depth"] = depth };

        var response = await harness.SendAsync("REPORT", CalDavConformance.Path, Multiget(Href + "ev1.ics"), headers: headers);

        response.Status.Should().Be(207);
        response.Prop(Href + "ev1.ics", DavNames.D.GetETag).Should().NotBeNull();
    }
}
