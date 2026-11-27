using System.Diagnostics;
using System.Globalization;
using AwesomeAssertions;
using Elarion.Calendars;
using Xunit;
using static Elarion.Tests.Calendars.CalendarRecurrenceRfcTests;
using static Elarion.Tests.Calendars.CalendarTestFiles;

namespace Elarion.Tests.Calendars;

/// <summary>
/// Recurrence edge cases of <see cref="CalendarRecurrence"/> ported from sabre/vobject's RRuleIteratorTest,
/// EventIteratorTest and their regression tests, and from Radicale, Xandikos and Cyrus where they test the engine. Where
/// the projects disagree, the test pins Elarion's answer and says so.
/// </summary>
public sealed class CalendarRecurrenceConformanceTests {
    /// <summary>The occurrences of the calendar of <paramref name="components"/> in the window, as local wall times <c>yyyyMMddTHHmm</c>.</summary>
    private static async Task<string[]> WallTimesAsync(DateTimeOffset from, DateTimeOffset to, params string[][] components) =>
        [.. (await ExpandAsync(from, to, components)).Occurrences.Select(static o => Wall(o.Start.Value))];

    private static async Task<CalendarOccurrences> ExpandAsync(DateTimeOffset from, DateTimeOffset to, params string[][] components) {
        var read = await ReadAsync(Calendar([.. components.SelectMany(static c => c)]));
        return read.Expand(from, to);
    }

    private static string Wall(DateTime value) => value.ToString("yyyyMMdd'T'HHmm", CultureInfo.InvariantCulture);

    /// <summary>The occurrences' instants in UTC, <c>yyyyMMddTHHmm</c>.</summary>
    private static async Task<string[]> UtcTimesAsync(DateTimeOffset from, DateTimeOffset to, params string[][] components) =>
        [.. (await ExpandAsync(from, to, components)).Occurrences.Select(static o => Wall(o.StartUtc!.Value.UtcDateTime))];

    /// <summary>sabre R20 (testMonthlyEndOfMonth): months without a 31st are skipped, not clamped, and do not consume COUNT.</summary>
    [Fact]
    public async Task MonthlyOnThe31st_SkipsShortMonths_WithoutConsumingCount() {
        var times = await WallTimesAsync(Utc(2011, 1, 1), Utc(2017, 1, 1),
            Event("UID:a", "DTSTART:20111231T090000Z", "RRULE:FREQ=MONTHLY;INTERVAL=2;COUNT=12"));

        times.Should().Equal("20111231T0900", "20120831T0900", "20121031T0900", "20121231T0900", "20130831T0900", "20131031T0900",
            "20131231T0900", "20140831T0900", "20141031T0900", "20141231T0900", "20150831T0900", "20151031T0900");
    }

    /// <summary>sabre R21 (testMonthlyByMonthDay): a negative BYMONTHDAY counts from the month's end; an invalid 31st is skipped.</summary>
    [Fact]
    public async Task NegativeAndInvalidMonthDays_CountFromTheEnd_OrAreSkipped() {
        var times = await WallTimesAsync(Utc(2011, 1, 1), Utc(2013, 1, 1),
            Event("UID:a", "DTSTART:20110101T090000Z", "RRULE:FREQ=MONTHLY;INTERVAL=5;COUNT=9;BYMONTHDAY=1,31,-7"));
        var lastDays = await WallTimesAsync(Utc(2012, 1, 1), Utc(2013, 1, 1),
            Event("UID:b", "DTSTART:20120131T090000Z", "RRULE:FREQ=MONTHLY;BYMONTHDAY=-1;COUNT=4"));

        times.Should().Equal("20110101T0900", "20110125T0900", "20110131T0900", "20110601T0900", "20110624T0900", "20111101T0900",
            "20111124T0900", "20120401T0900", "20120424T0900");
        lastDays.Should().Equal(["20120131T0900", "20120229T0900", "20120331T0900", "20120430T0900"], "BYMONTHDAY=-1 is February 29 in a leap year");
    }

    /// <summary>sabre R27 (testMonthlyByDayBySetPos); Cyrus E16: BYSETPOS with a negative position picks the first and last weekday.</summary>
    [Fact]
    public async Task BySetPos_FirstAndLastWeekdayOfEachMonth() {
        var times = await WallTimesAsync(Utc(2011, 1, 1), Utc(2012, 1, 1),
            Event("UID:a", "DTSTART:20110103T090000Z", "RRULE:FREQ=MONTHLY;COUNT=10;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=1,-1"));
        var year = await ExpandAsync(Utc(2025, 1, 1), Utc(2026, 1, 1),
            Event("UID:b", "DTSTART:20250101T000000Z", "DURATION:PT5M", "RRULE:FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=1,-1"));

        times.Should().Equal("20110103T0900", "20110131T0900", "20110201T0900", "20110228T0900", "20110301T0900", "20110331T0900",
            "20110401T0900", "20110429T0900", "20110502T0900", "20110531T0900");
        year.Occurrences.Should().HaveCount(24);
    }

    /// <summary>
    /// sabre R67 (SetPosHangTest): a negative BYSETPOS with a DTSTART that is not on the rule, in a zone without a VTIMEZONE,
    /// each instance in UTC with its own offset.
    /// </summary>
    [Fact]
    public async Task NegativeBySetPos_InCopenhagen_GivesEachInstanceItsOffset() {
        TimeZones.Get("Europe/Copenhagen");
        var occurrences = (await ExpandAsync(Utc(2015, 1, 1), Utc(2016, 1, 1),
            Event("UID:a", "DTSTART;TZID=Europe/Copenhagen:20150101T170000", "RRULE:FREQ=MONTHLY;BYDAY=TH;BYSETPOS=-2"))).Occurrences;

        occurrences.Select(static o => o.StartUtc!.Value.ToString("MMdd'T'HH", CultureInfo.InvariantCulture)).Should().Equal(
            "0101T16", "0122T16", "0219T16", "0319T16", "0423T15", "0521T15", "0618T15", "0723T15", "0820T15", "0917T15", "1022T15",
            "1119T16", "1224T16");
    }

    /// <summary>sabre R29 (testYearlyLeapYear): February 29 recurs only in leap years.</summary>
    [Fact]
    public async Task YearlyOnFebruary29_RecursOnlyInLeapYears() {
        var times = await WallTimesAsync(Utc(2012, 1, 1), Utc(2030, 1, 1),
            Event("UID:a", "DTSTART:20120229T090000Z", "RRULE:FREQ=YEARLY;COUNT=3"));

        times.Should().Equal("20120229T0900", "20160229T0900", "20200229T0900");
    }

    /// <summary>sabre R05, R24 (testDaily, testMonthlyByDayUntil): an instance equal to UNTIL is the last one.</summary>
    [Fact]
    public async Task InstanceEqualToUntil_IsIncluded() {
        var daily = await WallTimesAsync(Utc(2011, 10, 1), Utc(2012, 1, 1),
            Event("UID:a", "DTSTART:20111007T000000Z", "RRULE:FREQ=DAILY;INTERVAL=3;UNTIL=20111025T000000Z"));
        var monthly = await WallTimesAsync(Utc(2021, 2, 1), Utc(2022, 1, 1),
            Event("UID:b", "DTSTART:20210210T000000Z", "RRULE:FREQ=MONTHLY;BYDAY=WE;WKST=WE;UNTIL=20210317T000000Z"));

        daily.Should().HaveCount(7).And.EndWith("20111025T0000");
        monthly.Should().EndWith("20210317T0000").And.HaveCount(6);
    }

    /// <summary>
    /// Xandikos Q58 (Evolution, iPhone and Google UNTILs); sabre R47 (testUntilBeginHasTimezone): a floating UNTIL is read
    /// in DTSTART's zone, a DATE UNTIL covers its whole day, and a UTC UNTIL bounds an all-day series.
    /// </summary>
    [Fact]
    public async Task UntilOfAnotherValueType_IsReadAsProducersMeanIt() {
        TimeZones.Get("Europe/Amsterdam");
        TimeZones.Get("America/New_York");
        var floatingUntil = await WallTimesAsync(Utc(2009, 8, 1), Utc(2009, 9, 1),
            Event("UID:a", "DTSTART;TZID=Europe/Amsterdam:20090805T153000", "RRULE:FREQ=DAILY;UNTIL=20090807T153000"));
        var weekly = await WallTimesAsync(Utc(2013, 9, 1), Utc(2014, 1, 1),
            Event("UID:b", "DTSTART;TZID=America/New_York:20130923T183000", "RRULE:FREQ=WEEKLY;UNTIL=20131118T183000"));
        var dateUntil = await WallTimesAsync(Utc(2026, 1, 1), Utc(2026, 3, 1),
            Event("UID:c", "DTSTART;TZID=America/New_York:20260122T100000", "RRULE:FREQ=DAILY;UNTIL=20260131"));
        var allDay = await WallTimesAsync(Utc(2017, 1, 1), Utc(2030, 1, 1),
            Event("UID:d", "DTSTART;VALUE=DATE:20170626", "RRULE:FREQ=YEARLY;UNTIL=20220625T000000Z"));

        floatingUntil.Should().Equal("20090805T1530", "20090806T1530", "20090807T1530");
        weekly.Should().EndWith("20131118T1830");
        dateUntil.Should().HaveCount(10).And.EndWith("20260131T1000");
        allDay.Should().Equal("20170626T0000", "20180626T0000", "20190626T0000", "20200626T0000", "20210626T0000");
    }

    /// <summary>
    /// sabre R48 (testUntilBeforeDtStart: DTSTART alone); Radicale E28 refuses such an object, which a read-only server
    /// cannot. RFC 5545 §3.8.5.3 makes DTSTART the first instance, and Elarion counts a DTSTART that is off the rule (see
    /// <see cref="StartOffTheRule_IsTheFirstInstance_AndCounts"/>), so a rule whose UNTIL lies before DTSTART leaves DTSTART
    /// alone rather than nothing.
    /// </summary>
    [Fact]
    public async Task UntilBeforeTheStart_LeavesTheStartAlone() {
        var times = await WallTimesAsync(Utc(2013, 1, 1), Utc(2015, 1, 1),
            Event("UID:a", "DTSTART:20140802T000000Z", "RRULE:FREQ=DAILY;UNTIL=20140101T000000Z"));

        times.Should().Equal("20140802T0000");
    }

    /// <summary>sabre R56 (testComplexExclusions): EXDATEs (a list and a second property) consume COUNT.</summary>
    [Fact]
    public async Task ExceptionDates_ConsumeCount() {
        TimeZones.Get("America/Toronto");
        var times = await WallTimesAsync(Utc(2011, 1, 1), Utc(2025, 1, 1), Event("UID:a",
            "DTSTART;TZID=America/Toronto:20110110T135020", "RRULE:FREQ=YEARLY;COUNT=10",
            "EXDATE;TZID=America/Toronto:20120110T135020,20140110T135020", "EXDATE;TZID=America/Toronto:20160110T135020"));

        times.Select(static t => t[..4]).Should().Equal("2011", "2013", "2015", "2017", "2018", "2019", "2020");
    }

    /// <summary>sabre R13, R40, X17 (testWeeklyByDay, testYearlyByDayByWeekNo): a DTSTART that is not on the rule is the first instance and counts.</summary>
    [Fact]
    public async Task StartOffTheRule_IsTheFirstInstance_AndCounts() {
        var weekly = await WallTimesAsync(Utc(2014, 7, 1), Utc(2015, 1, 1),
            Event("UID:a", "DTSTART:20140801T090000Z", "RRULE:FREQ=WEEKLY;COUNT=4;BYDAY=MO;WKST=SA"));
        var weekNumbers = await WallTimesAsync(Utc(2021, 1, 1), Utc(2022, 1, 1),
            Event("UID:b", "DTSTART:20210101T090000Z", "RRULE:FREQ=YEARLY;COUNT=3;BYDAY=MO;BYWEEKNO=13,15,50"));

        weekly.Should().Equal("20140801T0900", "20140804T0900", "20140811T0900", "20140818T0900");
        weekNumbers.Should().Equal("20210101T0900", "20210329T0900", "20210412T0900");
    }

    /// <summary>sabre R45 (testYearlyStartDateNotOnRRuleList): occurrences of DTSTART's own year after DTSTART are still produced.</summary>
    [Fact]
    public async Task StartOffTheRule_KeepsTheRestOfItsYear() {
        var times = await WallTimesAsync(Utc(2023, 1, 1), Utc(2026, 1, 1),
            Event("UID:a", "DTSTART:20230601T090000Z", "RRULE:FREQ=YEARLY;BYMONTH=6;BYDAY=-1FR;UNTIL=20250901T000000Z"));

        times.Should().Equal("20230601T0900", "20230630T0900", "20240628T0900", "20250627T0900");
    }

    /// <summary>sabre R50 (testMinusFifthThursday): a -5TH that a month lacks is skipped.</summary>
    [Fact]
    public async Task NegativeOrdinalAMonthLacks_IsSkipped() {
        var times = await WallTimesAsync(Utc(2015, 1, 1), Utc(2016, 1, 1),
            Event("UID:a", "DTSTART:20150101T090000Z", "RRULE:FREQ=MONTHLY;BYDAY=-4TH,-5TH;COUNT=4"));

        times.Should().Equal("20150101T0900", "20150108T0900", "20150205T0900", "20150305T0900");
    }

    /// <summary>sabre R31, R33 (testYearlyByMonthByDay, testYearlyByYearDayImmutable): ordinal BYDAY within BYMONTH, and INTERVAL from DTSTART's year.</summary>
    [Fact]
    public async Task YearlyRules_ScopeOrdinalsToTheMonth_AndCountIntervalsFromTheStart() {
        var months = await WallTimesAsync(Utc(2011, 1, 1), Utc(2017, 1, 1),
            Event("UID:a", "DTSTART:20110404T090000Z", "RRULE:FREQ=YEARLY;INTERVAL=5;BYMONTH=4,10;BYDAY=1MO,-1SU"));
        var yearDay = await WallTimesAsync(Utc(2011, 1, 1), Utc(2016, 1, 1),
            Event("UID:b", "DTSTART:20110710T090000Z", "RRULE:FREQ=YEARLY;INTERVAL=2;BYYEARDAY=190"));

        months.Should().Equal("20110404T0900", "20110424T0900", "20111003T0900", "20111030T0900", "20160404T0900", "20160424T0900",
            "20161003T0900", "20161030T0900");
        yearDay.Should().Equal("20110710T0900", "20130709T0900", "20150709T0900");
    }

    /// <summary>
    /// sabre R37, R38 (testYearlyByYearDayLargeNegative, MaxNegative). R38 pins RFC 5545's reading rather than sabre's: day
    /// -366 exists only in a leap year, so it does not spill into the previous year's December 31.
    /// </summary>
    [Fact]
    public async Task NegativeYearDays_CountFromTheYearsEnd() {
        var minus365 = await WallTimesAsync(Utc(2001, 1, 1), Utc(2006, 1, 1),
            Event("UID:a", "DTSTART:20010101T090000Z", "RRULE:FREQ=YEARLY;BYYEARDAY=-365"));
        var minus366 = await WallTimesAsync(Utc(2001, 1, 1), Utc(2009, 1, 1),
            Event("UID:b", "DTSTART:20010101T090000Z", "RRULE:FREQ=YEARLY;BYYEARDAY=-366"));

        minus365.Select(static t => t[..8]).Should().Equal("20010101", "20020101", "20030101", "20040102", "20050101");
        minus366.Select(static t => t[..8]).Should().Equal("20010101", "20040101", "20080101");
    }

    /// <summary>
    /// sabre R41 (testNegativeValidByWeekNo). The catalogue lists 2012-08-07/10 for week -20; 2012 has 52 ISO weeks, so week
    /// -20 is week 33 (from Monday 2012-08-13), and 2013's is the week from Monday 2013-08-12: this test pins that RFC 5545
    /// reading.
    /// </summary>
    [Fact]
    public async Task NegativeWeekNumbers_CountFromTheYearsLastWeek() {
        var negative = await WallTimesAsync(Utc(2012, 1, 1), Utc(2014, 1, 1),
            Event("UID:a", "DTSTART:20120814T090000Z", "RRULE:FREQ=YEARLY;BYWEEKNO=-20;BYDAY=TU,FR"));

        negative.Should().Equal("20120814T0900", "20120817T0900", "20130813T0900", "20130816T0900");
    }

    /// <summary>
    /// sabre R41 (testValidByWeekNoByDayDefault): without BYDAY, BYWEEKNO takes DTSTART's weekday, as RFC 5545 §3.3.10 derives
    /// what a rule leaves out from DTSTART ("FREQ=YEARLY;BYMONTH=1" takes DTSTART's day of the month).
    /// </summary>
    [Fact]
    public async Task WeekNumberWithoutByDay_TakesTheStartsWeekday() {
        var byDefault = await WallTimesAsync(Utc(2011, 1, 1), Utc(2014, 1, 1),
            Event("UID:b", "DTSTART:20110516T090000Z", "RRULE:FREQ=YEARLY;BYWEEKNO=20"));

        byDefault.Should().Equal("20110516T0900", "20120514T0900", "20130513T0900");
    }

    /// <summary>sabre R08, R09, R66 (testDailyByDay, testDailyByMonth, ByMonthInDailyTest): BYDAY and BYMONTH limit a DAILY rule, across years.</summary>
    [Fact]
    public async Task DailyRules_AreLimitedByDayAndMonth() {
        var byDay = await WallTimesAsync(Utc(2011, 10, 1), Utc(2011, 11, 9),
            Event("UID:a", "DTSTART:20111007T090000Z", "RRULE:FREQ=DAILY;INTERVAL=2;BYDAY=TU,WE,FR"));
        var byMonth = await WallTimesAsync(Utc(2013, 9, 28), Utc(2014, 9, 11),
            Event("UID:b", "DTSTART:20070925T090000Z", "RRULE:FREQ=DAILY;BYMONTH=9,10;BYDAY=SU"));

        byDay.Should().Equal("20111007T0900", "20111011T0900", "20111019T0900", "20111021T0900", "20111025T0900", "20111102T0900",
            "20111104T0900", "20111108T0900");
        byMonth.Should().Equal("20130929T0900", "20131006T0900", "20131013T0900", "20131020T0900", "20131027T0900", "20140907T0900");
    }

    /// <summary>
    /// Cyrus E16 (calendar_query_expand_limit): several RRULE properties on one component add up, and BYHOUR expands a
    /// DAILY rule.
    /// </summary>
    [Fact]
    public async Task SeveralRules_AddUp() {
        var twoRules = await ExpandAsync(Utc(2025, 1, 1), Utc(2025, 1, 8), Event("UID:a", "DTSTART:20250101T030000Z", "DURATION:PT5M",
            "RRULE:FREQ=DAILY;BYHOUR=3,9", "RRULE:FREQ=DAILY;BYHOUR=15,21"));
        var byHour = await ExpandAsync(Utc(2025, 1, 1), Utc(2025, 4, 1),
            Event("UID:b", "DTSTART:20250101T000000Z", "DURATION:PT5M", "RRULE:FREQ=DAILY;BYHOUR=0,6,12,18"));

        twoRules.Occurrences.Should().HaveCount(28);
        byHour.Occurrences.Should().HaveCount(360);
    }

    public static TheoryData<string, string> ImpossibleRules => new() {
        { "20110104T090000Z", "FREQ=MONTHLY;BYDAY=2TU;BYSETPOS=2" },
        { "20110104T090000Z", "FREQ=YEARLY;BYMONTH=5;BYSETPOS=3;BYMONTHDAY=3" },
        { "20220315T090000Z", "FREQ=DAILY;INTERVAL=7;BYDAY=MO" },
        { "20210210T090000Z", "FREQ=MONTHLY;BYDAY=2WE;BYMONTHDAY=2;UNTIL=20210317T000000Z" },
        { "20071004T090000Z", "FREQ=MONTHLY;UNTIL=20071030T035959Z;BYDAY=5TU" },
    };

    /// <summary>
    /// sabre R10, R25, R44, R51, R52 (testDailyBySetPosLoop, testMonthlyByDayUntilWithImpossibleNextOccurrence,
    /// testYearlyBySetPosLoop, testNeverEnding, testFifthTuesdayProblem): a rule with no further instance terminates
    /// quickly, leaving DTSTART at most.
    /// </summary>
    [Theory]
    [MemberData(nameof(ImpossibleRules))]
    public async Task RuleWithoutFurtherInstances_Terminates(string start, string rule) {
        var watch = Stopwatch.StartNew();

        var occurrences = await ExpandAsync(Utc(2007, 1, 1), Utc(2107, 1, 1), Event("UID:a", "DTSTART:" + start, "RRULE:" + rule));

        occurrences.Occurrences.Count.Should().BeLessThanOrEqualTo(1);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    /// <summary>sabre R53 (testFastForwardTooFar): a window far past UNTIL answers nothing at once.</summary>
    [Fact]
    public async Task WindowPastUntil_AnswersNothing() {
        var watch = Stopwatch.StartNew();

        var occurrences = await ExpandAsync(Utc(2012, 1, 1), new DateTimeOffset(2999, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Event("UID:a", "DTSTART:20090421T090000Z", "RRULE:FREQ=WEEKLY;BYDAY=MO;UNTIL=20090704T000000Z"));

        occurrences.Occurrences.Should().BeEmpty();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// sabre R22, R28 (testMonthlyByMonthDayDstTransition, testMonthlyOnDstTransition): a wall time that the spring change
    /// skips moves forward by the gap, the 31st still skips short months, and the next instance returns to its wall time.
    /// </summary>
    [Fact]
    public async Task WallTimeInTheSpringGap_MovesForwardByTheGap() {
        TimeZones.Get("Europe/Zurich");
        var monthly = await UtcTimesAsync(Utc(2024, 1, 1), Utc(2025, 1, 1),
            Event("UID:a", "DTSTART;TZID=Europe/Zurich:20240131T021500", "RRULE:FREQ=MONTHLY;COUNT=5"));
        var monthDays = await UtcTimesAsync(Utc(2023, 3, 1), Utc(2023, 5, 1),
            Event("UID:b", "DTSTART;TZID=Europe/Zurich:20230301T021500", "RRULE:FREQ=MONTHLY;BYMONTHDAY=1,26"));

        // 2024-03-31 and 2023-03-26 are the spring change: 02:15 does not exist and becomes 03:15 CEST, 01:15Z.
        monthly.Should().Equal("20240131T0115", "20240331T0115", "20240531T0015", "20240731T0015", "20240831T0015");
        monthDays.Should().Equal("20230301T0115", "20230326T0115", "20230401T0015", "20230426T0015");
    }

    /// <summary>
    /// sabre R17 (testWeeklyByDayByHourOnDstTransition) skips a BYHOUR that the spring change removes, unlike a DTSTART-derived
    /// time (R11), and says the RFC is silent. Pins Elarion's choice: a nonexistent time from BYHOUR moves forward by the gap
    /// like any other (RFC 5545 §3.3.5), so the day keeps both instances.
    /// </summary>
    [Fact]
    public async Task ByHourInTheSpringGap_MovesForwardLikeAnyWallTime() {
        TimeZones.Get("Europe/Zurich");
        var times = await UtcTimesAsync(Utc(2023, 3, 25), Utc(2023, 3, 27),
            Event("UID:a", "DTSTART;TZID=Europe/Zurich:20230311T020000", "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=SA,SU;BYHOUR=2,14"));

        // 02:00 on 2023-03-26 does not exist; 03:00 CEST is 01:00Z.
        times.Should().Equal("20230325T0100", "20230325T1300", "20230326T0100", "20230326T1200");
    }

    /// <summary>
    /// sabre R58, R59, R63, R64 (testOverriddenEvent, testOverriddenEvent2, Issue50Test, OverrideDurationTest): overrides sort
    /// by their new start, may share a day with a regular instance or move before their slot, and bring their own end.
    /// </summary>
    [Fact]
    public async Task Overrides_SortByTheirNewStart_AndBringTheirOwnEnd() {
        var occurrences = (await ExpandAsync(Utc(2012, 1, 1), Utc(2012, 2, 1),
            Event("UID:a", "DTSTART:20120107T120000Z", "DTEND:20120107T130000Z", "RRULE:FREQ=DAILY;COUNT=10", "SUMMARY:Event 1"),
            Event("UID:a", "RECURRENCE-ID:20120110T120000Z", "DTSTART:20120110T140000Z", "DTEND:20120110T150000Z", "SUMMARY:Event 2"),
            Event("UID:a", "RECURRENCE-ID:20120113T120000Z", "DTSTART:20120115T140000Z", "DTEND:20120115T180000Z", "SUMMARY:Event 3"),
            Event("UID:a", "RECURRENCE-ID:20120114T120000Z", "DTSTART:20120114T120000Z", "DTEND:20120114T130000Z", "SUMMARY:Same slot"))).Occurrences;
        var earlier = await WallTimesAsync(Utc(2012, 1, 1), Utc(2012, 2, 1),
            Event("UID:b", "DTSTART:20120112T090000Z", "RRULE:FREQ=WEEKLY;COUNT=3"),
            Event("UID:b", "RECURRENCE-ID:20120119T090000Z", "DTSTART:20120113T090000Z"));

        occurrences.Select(static o => Wall(o.Start.Value)).Should().Equal("20120107T1200", "20120108T1200", "20120109T1200", "20120110T1400",
            "20120111T1200", "20120112T1200", "20120114T1200", "20120115T1200", "20120115T1400", "20120116T1200");
        occurrences.Single(static o => o.Item.Summary == "Event 3").EndUtc.Should().Be(new DateTimeOffset(2012, 1, 15, 18, 0, 0, TimeSpan.Zero));
        occurrences.Single(static o => o.Item.Summary == "Same slot").RecurrenceId.Value.Should().Be(new DateTime(2012, 1, 14, 12, 0, 0));
        earlier.Should().Equal("20120112T0900", "20120113T0900", "20120126T0900");
    }

    /// <summary>
    /// sabre R65 (vobject issue #3, testAllEventsArePresentInIterator): overrides that share the master's start do not
    /// collapse, and an override for a slot beyond COUNT is still an occurrence (Elarion's documented reading).
    /// </summary>
    [Fact]
    public async Task OverridesWithTheSameStart_DoNotCollapse() {
        var occurrences = (await ExpandAsync(Utc(2014, 1, 1), Utc(2014, 2, 1),
            Event("UID:a", "DTSTART:20140113T100000Z", "RRULE:FREQ=DAILY;COUNT=3"),
            Event("UID:a", "RECURRENCE-ID:20140114T100000Z", "DTSTART:20140113T100000Z"),
            Event("UID:a", "RECURRENCE-ID:20140115T100000Z", "DTSTART:20140113T100000Z"),
            Event("UID:a", "RECURRENCE-ID:20140116T100000Z", "DTSTART:20140113T100000Z"))).Occurrences;

        occurrences.Should().HaveCount(4).And.OnlyContain(static o => o.Start.Value == new DateTime(2014, 1, 13, 10, 0, 0));
    }

    /// <summary>
    /// sabre R61 (testNoMasterBadUID: sabre throws); Radicale Q43 (refuses two orphans); Cyrus F09 (accepts them). Pins
    /// Elarion's choice: each override without its master is one occurrence at its own start.
    /// </summary>
    [Fact]
    public async Task OverridesWithoutTheirMaster_AreEachOneOccurrence() {
        var occurrences = (await ExpandAsync(Utc(2023, 1, 1), Utc(2024, 1, 1),
            Event("UID:a", "RECURRENCE-ID:20230909T160000Z", "DTSTART:20230101T160000Z", "DURATION:PT1H"),
            Event("UID:a", "RECURRENCE-ID:20231231T160000Z", "DTSTART:20231231T160000Z", "DURATION:PT2H"))).Occurrences;

        occurrences.Select(static o => (o.StartUtc!.Value, o.EndUtc!.Value)).Should().Equal(
            (new DateTimeOffset(2023, 1, 1, 16, 0, 0, TimeSpan.Zero), new DateTimeOffset(2023, 1, 1, 17, 0, 0, TimeSpan.Zero)),
            (new DateTimeOffset(2023, 12, 31, 16, 0, 0, TimeSpan.Zero), new DateTimeOffset(2023, 12, 31, 18, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>sabre R62 (Issue48Test): an UNTIL in UTC bounds a zoned series, and an override moved past it is still an occurrence.</summary>
    [Fact]
    public async Task OverrideMovedPastUntil_IsStillAnOccurrence() {
        TimeZones.Get("Europe/Moscow");
        var times = await WallTimesAsync(Utc(2013, 7, 1), Utc(2013, 8, 1),
            Event("UID:a", "DTSTART;TZID=Europe/Moscow:20130710T120000", "RRULE:FREQ=DAILY;UNTIL=20130712T195959Z"),
            Event("UID:a", "RECURRENCE-ID;TZID=Europe/Moscow:20130711T120000", "DTSTART;TZID=Europe/Moscow:20130713T120000"));

        times.Should().Equal("20130710T1200", "20130712T1200", "20130713T1200");
    }

    /// <summary>sabre R54, R55 (RDateIteratorTest, testRDATE): RDATEs add instances, years after a DATE start too.</summary>
    [Fact]
    public async Task RecurrenceDates_AddInstances() {
        var list = await WallTimesAsync(Utc(2014, 1, 1), Utc(2015, 1, 1),
            Event("UID:a", "DTSTART:20140801T000000Z", "RDATE:20140901T000000Z,20141001T000000Z"));
        var dates = await WallTimesAsync(Utc(2011, 1, 1), Utc(2015, 1, 1),
            Event("UID:b", "DTSTART;VALUE=DATE:20111007", "RDATE;VALUE=DATE:20140807,20140808"));

        list.Should().Equal("20140801T0000", "20140901T0000", "20141001T0000");
        dates.Select(static t => t[..8]).Should().Equal("20111007", "20140807", "20140808");
    }

    /// <summary>
    /// sabre R54 (testTimezone: a floating RDATE takes DTSTART's zone) versus Elarion, where a floating value is placed in the
    /// floating zone of the expansion. Pins Elarion's choice: the floating RDATE keeps its wall time and is converted
    /// through <see cref="CalendarExpandOptions.FloatingTimeZone"/>, not through DTSTART's zone.
    /// </summary>
    [Fact]
    public async Task FloatingRecurrenceDate_OfAZonedSeries_IsPlacedInTheFloatingZone() {
        TimeZones.Get("Europe/Berlin");
        var read = await ReadAsync(Calendar(Event("UID:a", "DTSTART;TZID=Europe/Berlin:20140801T120000", "RDATE:20140901T120000")));

        var occurrences = read.Expand(Utc(2014, 1, 1), Utc(2015, 1, 1), new CalendarExpandOptions { FloatingTimeZone = TimeZoneInfo.Utc }, cancellationToken: TestContext.Current.CancellationToken).Occurrences;

        occurrences.Should().HaveCount(2);
        occurrences[1].Start.Value.Should().Be(new DateTime(2014, 9, 1, 12, 0, 0));
        occurrences[1].StartUtc.Should().Be(new DateTimeOffset(2014, 9, 1, 12, 0, 0, TimeSpan.Zero), "12:00 in the floating zone, not 12:00 in Berlin");
    }

    /// <summary>
    /// Xandikos E23, E24 (test_expand_with_multiple_exdate_properties, MixedDateDatetimeTests): EXDATE lists and repeated EXDATE
    /// lines add up, and a DATE EXDATE removes that day's instance of a DATE-TIME series (and the reverse).
    /// </summary>
    [Fact]
    public async Task ExceptionDates_OfEitherValueType_RemoveTheirDay() {
        var lines = await WallTimesAsync(Utc(2024, 1, 1), Utc(2024, 2, 1), Event("UID:a", "DTSTART:20240101T100000Z", "RRULE:FREQ=DAILY;COUNT=7",
            "EXDATE:20240102T100000Z", "EXDATE:20240103T100000Z", "EXDATE:20240104T100000Z"));
        var dateOnTime = await WallTimesAsync(Utc(2024, 1, 1), Utc(2024, 2, 1),
            Event("UID:b", "DTSTART:20240101T100000Z", "RRULE:FREQ=DAILY;COUNT=5", "EXDATE;VALUE=DATE:20240102"));
        var timeOnDate = await WallTimesAsync(Utc(2024, 1, 1), Utc(2024, 2, 1),
            Event("UID:c", "DTSTART;VALUE=DATE:20240101", "RRULE:FREQ=DAILY;COUNT=5", "EXDATE:20240102T000000Z"));

        lines.Should().HaveCount(4);
        dateOnTime.Should().Equal("20240101T1000", "20240103T1000", "20240104T1000", "20240105T1000");
        timeOnDate.Select(static t => t[..8]).Should().Equal("20240101", "20240103", "20240104", "20240105");
    }

    public static TheoryData<string> InvalidRules => new() {
        "FREQ=YEARLY;BYMONTH=0",
        "FREQ=YEARLY;BYMONTH=bla",
        "FREQ=YEARLY;BYMONTH=0,bla",
        "FREQ=YEARLY;BYMONTH=",
        "FREQ=YEARLY;BYYEARDAY=390",
        "FREQ=YEARLY;BYYEARDAY=0",
        "FREQ=YEARLY;BYWEEKNO=54",
        "FREQ=DAILY;INTERVAL=0",
        "FREQ=SMONTHLY",
        "FREQ=MONTHLY;BYDAY=0MO",
    };

    /// <summary>sabre R32, R39, R42, R46 (invalid BYMONTH, BYYEARDAY, BYWEEKNO, INTERVAL, FREQ and BYDAY offset): such a rule does not parse.</summary>
    [Theory]
    [MemberData(nameof(InvalidRules))]
    public void InvalidRule_DoesNotParse(string rule) {
        CalendarRecurrenceRule.TryParse(rule, out _).Should().BeFalse();
    }
}
