using System.Globalization;
using AwesomeAssertions;
using Elarion.Calendars;
using Xunit;

namespace Elarion.Tests.Calendars;

/// <summary>
/// The examples of RFC 5545 section 3.8.5.3 (spec 0004 SC-003), each expanded from its DTSTART in America/New_York and
/// compared by wall-clock time.
/// </summary>
public sealed class CalendarRecurrenceRfcTests {
    public static TheoryData<string, string, string, string[]> Examples => new() {
        { "Daily for 10 occurrences", "19970902T090000", "FREQ=DAILY;COUNT=10",
            ["19970902T0900", "19970903T0900", "19970904T0900", "19970905T0900", "19970906T0900", "19970907T0900", "19970908T0900",
                "19970909T0900", "19970910T0900", "19970911T0900"] },
        { "Every other day", "19970902T090000", "FREQ=DAILY;INTERVAL=2",
            ["19970902T0900", "19970904T0900", "19970906T0900", "19970908T0900"] },
        { "Every 10 days, 5 occurrences", "19970902T090000", "FREQ=DAILY;INTERVAL=10;COUNT=5",
            ["19970902T0900", "19970912T0900", "19970922T0900", "19971002T0900", "19971012T0900"] },
        { "Weekly for 10 occurrences", "19970902T090000", "FREQ=WEEKLY;COUNT=10",
            ["19970902T0900", "19970909T0900", "19970916T0900", "19970923T0900", "19970930T0900", "19971007T0900", "19971014T0900",
                "19971021T0900", "19971028T0900", "19971104T0900"] },
        { "Weekly on Tuesday and Thursday for five weeks", "19970902T090000", "FREQ=WEEKLY;COUNT=10;WKST=SU;BYDAY=TU,TH",
            ["19970902T0900", "19970904T0900", "19970909T0900", "19970911T0900", "19970916T0900", "19970918T0900", "19970923T0900",
                "19970925T0900", "19970930T0900", "19971002T0900"] },
        { "Every other week on Monday, Wednesday and Friday until 24 December 1997", "19970901T090000",
            "FREQ=WEEKLY;INTERVAL=2;UNTIL=19971224T000000Z;WKST=SU;BYDAY=MO,WE,FR",
            ["19970901T0900", "19970903T0900", "19970905T0900", "19970915T0900", "19970917T0900", "19970919T0900", "19970929T0900",
                "19971001T0900", "19971003T0900", "19971013T0900", "19971015T0900", "19971017T0900", "19971027T0900", "19971029T0900",
                "19971031T0900", "19971110T0900", "19971112T0900", "19971114T0900", "19971124T0900", "19971126T0900", "19971128T0900",
                "19971208T0900", "19971210T0900", "19971212T0900", "19971222T0900"] },
        { "Monthly on the first Friday for 10 occurrences", "19970905T090000", "FREQ=MONTHLY;COUNT=10;BYDAY=1FR",
            ["19970905T0900", "19971003T0900", "19971107T0900", "19971205T0900", "19980102T0900", "19980206T0900", "19980306T0900",
                "19980403T0900", "19980501T0900", "19980605T0900"] },
        { "Every other month on the first and last Sunday", "19970907T090000", "FREQ=MONTHLY;INTERVAL=2;COUNT=10;BYDAY=1SU,-1SU",
            ["19970907T0900", "19970928T0900", "19971102T0900", "19971130T0900", "19980104T0900", "19980125T0900", "19980301T0900",
                "19980329T0900", "19980503T0900", "19980531T0900"] },
        { "Monthly on the second-to-last Monday for 6 months", "19970922T090000", "FREQ=MONTHLY;COUNT=6;BYDAY=-2MO",
            ["19970922T0900", "19971020T0900", "19971117T0900", "19971222T0900", "19980119T0900", "19980216T0900"] },
        { "Monthly on the third-to-last day", "19970928T090000", "FREQ=MONTHLY;BYMONTHDAY=-3",
            ["19970928T0900", "19971029T0900", "19971128T0900", "19971229T0900", "19980129T0900", "19980226T0900"] },
        { "Monthly on the 2nd and 15th for 10 occurrences", "19970902T090000", "FREQ=MONTHLY;COUNT=10;BYMONTHDAY=2,15",
            ["19970902T0900", "19970915T0900", "19971002T0900", "19971015T0900", "19971102T0900", "19971115T0900", "19971202T0900",
                "19971215T0900", "19980102T0900", "19980115T0900"] },
        { "Monthly on the first and last day for 10 occurrences", "19970930T090000", "FREQ=MONTHLY;COUNT=10;BYMONTHDAY=1,-1",
            ["19970930T0900", "19971001T0900", "19971031T0900", "19971101T0900", "19971130T0900", "19971201T0900", "19971231T0900",
                "19980101T0900", "19980131T0900", "19980201T0900"] },
        { "Every 18 months on the 10th to 15th", "19970910T090000", "FREQ=MONTHLY;INTERVAL=18;COUNT=10;BYMONTHDAY=10,11,12,13,14,15",
            ["19970910T0900", "19970911T0900", "19970912T0900", "19970913T0900", "19970914T0900", "19970915T0900", "19990310T0900",
                "19990311T0900", "19990312T0900", "19990313T0900"] },
        { "Every Tuesday, every other month", "19970902T090000", "FREQ=MONTHLY;INTERVAL=2;BYDAY=TU",
            ["19970902T0900", "19970909T0900", "19970916T0900", "19970923T0900", "19970930T0900", "19971104T0900", "19971111T0900",
                "19971118T0900", "19971125T0900", "19980106T0900"] },
        { "Yearly in June and July for 10 occurrences", "19970610T090000", "FREQ=YEARLY;COUNT=10;BYMONTH=6,7",
            ["19970610T0900", "19970710T0900", "19980610T0900", "19980710T0900", "19990610T0900", "19990710T0900", "20000610T0900",
                "20000710T0900", "20010610T0900", "20010710T0900"] },
        { "Every other year in January, February and March", "19970310T090000", "FREQ=YEARLY;INTERVAL=2;COUNT=10;BYMONTH=1,2,3",
            ["19970310T0900", "19990110T0900", "19990210T0900", "19990310T0900", "20010110T0900", "20010210T0900", "20010310T0900",
                "20030110T0900", "20030210T0900", "20030310T0900"] },
        { "Every third year on the 1st, 100th and 200th day", "19970101T090000", "FREQ=YEARLY;INTERVAL=3;COUNT=10;BYYEARDAY=1,100,200",
            ["19970101T0900", "19970410T0900", "19970719T0900", "20000101T0900", "20000409T0900", "20000718T0900", "20030101T0900",
                "20030410T0900", "20030719T0900", "20060101T0900"] },
        { "Every 20th Monday of the year", "19970519T090000", "FREQ=YEARLY;BYDAY=20MO",
            ["19970519T0900", "19980518T0900", "19990517T0900"] },
        { "Monday of week 20", "19970512T090000", "FREQ=YEARLY;BYWEEKNO=20;BYDAY=MO",
            ["19970512T0900", "19980511T0900", "19990517T0900"] },
        { "Every Thursday in March", "19970313T090000", "FREQ=YEARLY;BYMONTH=3;BYDAY=TH",
            ["19970313T0900", "19970320T0900", "19970327T0900", "19980305T0900", "19980312T0900", "19980319T0900", "19980326T0900",
                "19990304T0900"] },
        { "Every Thursday in June, July and August", "19970605T090000", "FREQ=YEARLY;BYDAY=TH;BYMONTH=6,7,8",
            ["19970605T0900", "19970612T0900", "19970619T0900", "19970626T0900", "19970703T0900", "19970710T0900"] },
        { "The first Saturday that follows the first Sunday", "19970913T090000", "FREQ=MONTHLY;BYDAY=SA;BYMONTHDAY=7,8,9,10,11,12,13",
            ["19970913T0900", "19971011T0900", "19971108T0900", "19971213T0900", "19980110T0900", "19980207T0900", "19980307T0900",
                "19980411T0900", "19980509T0900", "19980613T0900"] },
        { "US presidential election day", "19961105T090000", "FREQ=YEARLY;INTERVAL=4;BYMONTH=11;BYDAY=TU;BYMONTHDAY=2,3,4,5,6,7,8",
            ["19961105T0900", "20001107T0900", "20041102T0900"] },
        { "The third instance of Tuesday, Wednesday or Thursday", "19970904T090000", "FREQ=MONTHLY;COUNT=3;BYDAY=TU,WE,TH;BYSETPOS=3",
            ["19970904T0900", "19971007T0900", "19971106T0900"] },
        { "The second-to-last weekday of the month", "19970929T090000", "FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-2",
            ["19970929T0900", "19971030T0900", "19971127T0900", "19971230T0900", "19980129T0900", "19980226T0900", "19980330T0900"] },
        { "Every 15 minutes for 6 occurrences", "19970902T090000", "FREQ=MINUTELY;INTERVAL=15;COUNT=6",
            ["19970902T0900", "19970902T0915", "19970902T0930", "19970902T0945", "19970902T1000", "19970902T1015"] },
        { "Every hour and a half for 4 occurrences", "19970902T090000", "FREQ=MINUTELY;INTERVAL=90;COUNT=4",
            ["19970902T0900", "19970902T1030", "19970902T1200", "19970902T1330"] },
        { "Every 20 minutes from 9:00 to 16:40, daily", "19970902T090000", "FREQ=DAILY;BYHOUR=9,10,11,12,13,14,15,16;BYMINUTE=0,20,40",
            ["19970902T0900", "19970902T0920", "19970902T0940", "19970902T1000"] },
        { "Every 20 minutes from 9:00 to 16:40, minutely", "19970902T090000", "FREQ=MINUTELY;INTERVAL=20;BYHOUR=9,10,11,12,13,14,15,16",
            ["19970902T0900", "19970902T0920", "19970902T0940", "19970902T1000"] },
        { "WKST=MO", "19970805T090000", "FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;WKST=MO",
            ["19970805T0900", "19970810T0900", "19970819T0900", "19970824T0900"] },
        { "WKST=SU", "19970805T090000", "FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;WKST=SU",
            ["19970805T0900", "19970817T0900", "19970819T0900", "19970831T0900"] },
        { "An invalid date is skipped", "20070115T090000", "FREQ=MONTHLY;BYMONTHDAY=15,30;COUNT=5",
            ["20070115T0900", "20070130T0900", "20070215T0900", "20070315T0900", "20070330T0900"] },
        { "Every day in January for 3 years", "19980101T090000",
            "FREQ=YEARLY;UNTIL=20000131T140000Z;BYMONTH=1;BYDAY=SU,MO,TU,WE,TH,FR,SA",
            ["19980101T0900", "19980102T0900", "19980103T0900"] },
    };

    [Theory]
    [MemberData(nameof(Examples))]
    public void RfcExample_ExpandsToItsOccurrences(string name, string start, string rule, string[] expected) {
        _ = name;
        var occurrences = ExpandNewYork(start, rule);

        occurrences.Take(expected.Length).Select(Format).Should().Equal(expected);
    }

    [Fact]
    public void DailyUntilDecember24_StopsAtTheLastDayBeforeIt() {
        var occurrences = ExpandNewYork("19970902T090000", "FREQ=DAILY;UNTIL=19971224T000000Z");

        occurrences.Should().HaveCount(113);
        Format(occurrences[^1]).Should().Be("19971223T0900");
    }

    [Fact]
    public void EveryDayInJanuaryForThreeYears_Has93Occurrences() {
        ExpandNewYork("19980101T090000", "FREQ=YEARLY;UNTIL=20000131T140000Z;BYMONTH=1;BYDAY=SU,MO,TU,WE,TH,FR,SA").Should().HaveCount(93);
    }

    [Fact]
    public void FridayThe13th_ExceptTheStart_FindsTheFridays() {
        var newYork = TimeZones.Get("America/New_York");
        var item = new CalendarEvent {
            Uid = "f13",
            Start = CalendarDateTime.Zoned(new DateTime(1997, 9, 2, 9, 0, 0), newYork),
            RecurrenceRules = { Rule("FREQ=MONTHLY;BYDAY=FR;BYMONTHDAY=13") },
            ExceptionDates = { CalendarDateTime.Zoned(new DateTime(1997, 9, 2, 9, 0, 0), newYork) },
        };

        var occurrences = CalendarRecurrence.Expand([item], Utc(1997, 1, 1), Utc(2001, 1, 1)).Occurrences;

        occurrences.Select(o => o.Start.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
            .Should().Equal("19980213", "19980313", "19981113", "19990813", "20001013");
    }

    internal static IReadOnlyList<DateTime> ExpandNewYork(string start, string rule) {
        var newYork = TimeZones.Get("America/New_York");
        var startValue = DateTime.ParseExact(start, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        var item = new CalendarEvent {
            Uid = "rfc",
            Start = CalendarDateTime.Zoned(startValue, newYork),
            RecurrenceRules = { Rule(rule) },
        };

        var result = CalendarRecurrence.Expand([item], Utc(1990, 1, 1), Utc(2010, 1, 1));
        // A rule without an end hits the occurrence cap over twenty years; the first occurrences are still exact.
        result.Errors.Should().OnlyContain(error => error.Code == CalendarErrorCodes.TooManyOccurrences);
        return [.. result.Occurrences.Select(o => o.Start.Value)];
    }

    internal static CalendarRecurrenceRule Rule(string text) {
        CalendarRecurrenceRule.TryParse(text, out var rule).Should().BeTrue();
        return rule!;
    }

    internal static DateTimeOffset Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private static string Format(DateTime value) => value.ToString("yyyyMMdd'T'HHmm", CultureInfo.InvariantCulture);
}
