using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Calendars;
using Xunit;
using static Elarion.Tests.Calendars.CalendarRecurrenceRfcTests;
using static Elarion.Tests.Calendars.CalendarTestFiles;

namespace Elarion.Tests.Calendars;

/// <summary>The scenarios "Time zones" and "Expand recurring events within a window" of spec 0004.</summary>
public sealed class CalendarTimeZoneAndRecurrenceTests {
    private static readonly string[] CustomZone = [
        "BEGIN:VTIMEZONE", "TZID:Customized Time Zone",
        "BEGIN:STANDARD", "DTSTART:16010101T030000", "TZOFFSETFROM:+0200", "TZOFFSETTO:+0100",
        "RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=10", "END:STANDARD",
        "BEGIN:DAYLIGHT", "DTSTART:16010101T020000", "TZOFFSETFROM:+0100", "TZOFFSETTO:+0200",
        "RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=3", "END:DAYLIGHT", "END:VTIMEZONE",
    ];

    [Fact]
    public async Task SystemZone_WinsOverTheFilesDefinition() {
        TimeZones.Get("Europe/Vienna");
        var result = await ReadAsync(Calendar([
            "BEGIN:VTIMEZONE", "TZID:Europe/Vienna", "BEGIN:STANDARD", "DTSTART:19700101T000000", "TZOFFSETFROM:+0500", "TZOFFSETTO:+0500",
            "END:STANDARD", "END:VTIMEZONE",
            .. Event("UID:a", "DTSTART;TZID=Europe/Vienna:20260701T090000"),
        ]));

        result.Events[0].Value!.Start!.Value.ToUtc().Should().Be(new DateTimeOffset(2026, 7, 1, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task CustomizedTimeZone_ConvertsThroughTheFilesDefinition() {
        var result = await ReadAsync(Calendar([
            .. Event("UID:a", "DTSTART;TZID=Customized Time Zone:20260701T090000"),
            .. Event("UID:b", "DTSTART;TZID=Customized Time Zone:20261201T090000"),
            .. CustomZone,
        ]));

        result.HasErrors.Should().BeFalse();
        result.Events[0].Value!.Start!.Value.ToUtc().Should().Be(new DateTimeOffset(2026, 7, 1, 7, 0, 0, TimeSpan.Zero));
        result.Events[1].Value!.Start!.Value.ToUtc().Should().Be(new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task SouthernHemisphereDefinition_ConvertsBothSeasons() {
        var result = await ReadAsync(Calendar([
            "BEGIN:VTIMEZONE", "TZID:My Sydney",
            "BEGIN:STANDARD", "DTSTART:20080406T030000", "TZOFFSETFROM:+1100", "TZOFFSETTO:+1000",
            "RRULE:FREQ=YEARLY;BYMONTH=4;BYDAY=1SU", "END:STANDARD",
            "BEGIN:DAYLIGHT", "DTSTART:20081005T020000", "TZOFFSETFROM:+1000", "TZOFFSETTO:+1100",
            "RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=1SU", "END:DAYLIGHT", "END:VTIMEZONE",
            .. Event("UID:a", "DTSTART;TZID=My Sydney:20260115T120000"),
            .. Event("UID:b", "DTSTART;TZID=My Sydney:20260715T120000"),
        ]));

        result.Events[0].Value!.Start!.Value.ToUtc().Should().Be(new DateTimeOffset(2026, 1, 15, 1, 0, 0, TimeSpan.Zero));
        result.Events[1].Value!.Start!.Value.ToUtc().Should().Be(new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task UnknownTimeZone_LeavesTheEventWithoutValue() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART;TZID=Nowhere/Atlantis:20260701T090000")));

        result.Events[0].HasValue.Should().BeFalse();
        var error = result.Events[0].Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(CalendarErrorCodes.UnknownTimeZone);
        error.TimeZoneId.Should().Be("Nowhere/Atlantis");
    }

    [Fact]
    public async Task FloatingStart_StaysFloating_AndConvertsOnlyInTheGivenZone() {
        var vienna = TimeZones.Get("Europe/Vienna");
        var result = await ReadAsync(Calendar(["X-WR-TIMEZONE:Europe/Berlin", .. Event("UID:a", "DTSTART:20261201T090000")]));

        var start = result.Events[0].Value!.Start!.Value;
        start.Kind.Should().Be(CalendarDateTimeKind.Floating);
        start.ToUtc().Should().BeNull();
        var occurrence = result.Expand(Utc(2026, 12, 1), Utc(2026, 12, 2), new CalendarExpandOptions { FloatingTimeZone = vienna }, cancellationToken: TestContext.Current.CancellationToken).Occurrences.Single();
        occurrence.StartUtc.Should().Be(new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero));
        result.Calendar.SuggestedTimeZone.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Europe/Vienna")]
    [InlineData("America/New_York")]
    [InlineData("Australia/Sydney")]
    [InlineData("America/Sao_Paulo")]
    [InlineData("Asia/Kolkata")]
    [InlineData("Pacific/Chatham")]
    [InlineData("Europe/Moscow")]
    public void SystemZone_ToVTimeZoneAndBack_AgreesEveryHourFrom1970To2037(string id) {
        var zone = TimeZones.Get(id);

        var component = CalendarTimeZones.ToComponent(zone);
        CalendarTimeZones.TryFromComponent(component, out var back, out var error).Should().BeTrue(error?.Message);

        // TimeZoneInfo evaluates rules per calendar year, so two shapes cannot be expressed exactly: the New Year hour
        // of a zone's first year that ends in daylight time, and a year with three different offsets (Moscow 1992).
        // The system's own tzdata changes representation in 2037, so the comparison stops there.
        var mismatches = new List<DateTime>();
        for (var t = new DateTime(1970, 1, 2, 0, 0, 0, DateTimeKind.Utc); t < new DateTime(2037, 1, 1, 0, 0, 0, DateTimeKind.Utc); t = t.AddHours(1)) {
            if (zone.GetUtcOffset(t) == back.GetUtcOffset(t)) continue;
            var local = t + zone.BaseUtcOffset;
            if ((local.Month == 1 && local.Day == 1) || (local.Month == 12 && local.Day == 31)) continue;
            if (ThreeOffsets(zone, local.Year)) continue;
            mismatches.Add(t);
        }

        mismatches.Should().BeEmpty();
    }

    private static bool ThreeOffsets(TimeZoneInfo zone, int year) {
        var offsets = new HashSet<TimeSpan>();
        for (var day = new DateTime(year, 1, 1, 12, 0, 0, DateTimeKind.Utc); day.Year == year; day = day.AddDays(1)) offsets.Add(zone.GetUtcOffset(day));
        return offsets.Count > 2;
    }

    [Fact]
    public void WeeklyRuleWithExceptionAndOverride_HasNineOccurrences_TheFifthMoved() {
        var vienna = TimeZones.Get("Europe/Vienna");
        var start = new DateTime(2026, 1, 6, 10, 0, 0);
        var master = new CalendarEvent {
            Uid = "s", Summary = "Standup", Start = CalendarDateTime.Zoned(start, vienna), Duration = TimeSpan.FromMinutes(30),
            RecurrenceRules = { Rule("FREQ=WEEKLY;BYDAY=TU;COUNT=10") },
            ExceptionDates = { CalendarDateTime.Zoned(start.AddDays(14), vienna) },
        };
        var moved = new CalendarEvent {
            Uid = "s", Summary = "Standup (moved)", RecurrenceId = CalendarDateTime.Zoned(start.AddDays(28), vienna),
            Start = CalendarDateTime.Zoned(start.AddDays(28).AddHours(2), vienna), Duration = TimeSpan.FromMinutes(30),
        };

        var occurrences = CalendarRecurrence.Expand([master, moved], Utc(2026, 1, 6), Utc(2027, 1, 6), cancellationToken: TestContext.Current.CancellationToken).Occurrences;

        occurrences.Should().HaveCount(9);
        occurrences[3].Item.Summary.Should().Be("Standup (moved)");
        occurrences[3].Start.Value.Should().Be(start.AddDays(28).AddHours(2));
        occurrences[3].Master.Should().BeSameAs(master);
    }

    [Fact]
    public void OutlookSecondTuesday_IsTheSecondTuesdayOfEveryMonth() {
        var dates = ExpandNewYork("19970114T090000", "FREQ=MONTHLY;BYDAY=TU;BYSETPOS=2;COUNT=12");

        dates.Should().HaveCount(12).And.OnlyContain(d => d.DayOfWeek == DayOfWeek.Tuesday && d.Day >= 8 && d.Day <= 14);
    }

    [Fact]
    public void DailyAt0230AcrossDaylightSavingChanges_MovesForwardInTheGap_AndTakesTheFirstOffsetInTheFold() {
        var vienna = TimeZones.Get("Europe/Vienna");
        var item = new CalendarEvent {
            Uid = "d", Start = CalendarDateTime.Zoned(new DateTime(2026, 3, 28, 2, 30, 0), vienna), RecurrenceRules = { Rule("FREQ=DAILY") },
        };

        var spring = CalendarRecurrence.Expand([item], Utc(2026, 3, 29), Utc(2026, 3, 30), cancellationToken: TestContext.Current.CancellationToken).Occurrences.Single();
        spring.StartUtc.Should().Be(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero)); // 03:30 CEST
        var autumn = CalendarRecurrence.Expand([item], Utc(2026, 10, 25), Utc(2026, 10, 26), cancellationToken: TestContext.Current.CancellationToken).Occurrences.Single();
        autumn.StartUtc.Should().Be(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero)); // 02:30 CEST, the first offset
    }

    [Fact]
    public void HourlyAcrossTheSpringGap_StaysHourlyInElapsedTime() {
        var vienna = TimeZones.Get("Europe/Vienna");
        var item = new CalendarEvent {
            Uid = "h", Start = CalendarDateTime.Zoned(new DateTime(2026, 3, 29, 0, 0, 0), vienna), RecurrenceRules = { Rule("FREQ=HOURLY;COUNT=5") },
        };

        var occurrences = CalendarRecurrence.Expand([item], Utc(2026, 3, 28), Utc(2026, 3, 30), cancellationToken: TestContext.Current.CancellationToken).Occurrences;

        occurrences.Select(o => o.StartUtc!.Value.Hour).Should().Equal(23, 0, 1, 2, 3);
    }

    [Fact]
    public void EveryMinuteForever_StopsAtTheOccurrenceCap() {
        var item = new CalendarEvent {
            Uid = "m", Start = CalendarDateTime.Utc(Utc(2026, 1, 1)), RecurrenceRules = { Rule("FREQ=MINUTELY") },
        };
        var watch = Stopwatch.StartNew();

        var result = CalendarRecurrence.Expand([item], Utc(2026, 1, 1), Utc(2036, 1, 1), cancellationToken: TestContext.Current.CancellationToken);

        result.Occurrences.Should().HaveCount(10_000);
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.TooManyOccurrences);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void FebruaryThirtieth_EndsWithARecurrenceLimitError_AndNoOccurrence() {
        var item = new CalendarEvent {
            Uid = "f", Start = CalendarDateTime.FromDate(new DateOnly(2026, 1, 30)), RecurrenceRules = { Rule("FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30") },
        };

        var result = CalendarRecurrence.Expand([item], Utc(2026, 1, 1), Utc(2126, 1, 1), cancellationToken: TestContext.Current.CancellationToken);

        result.Occurrences.Should().BeEmpty();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.RecurrenceLimit);
    }

    [Fact]
    public void RuleThatRarelyMatches_HitsTheIterationCap() {
        var item = new CalendarEvent {
            Uid = "r", Start = CalendarDateTime.Utc(Utc(2026, 1, 1)),
            RecurrenceRules = { Rule("FREQ=SECONDLY;BYMONTH=12;BYMONTHDAY=31;BYHOUR=23;BYMINUTE=59;BYSECOND=59") },
        };

        var result = CalendarRecurrence.Expand([item], Utc(2026, 1, 1), Utc(2030, 1, 1), new CalendarExpandOptions { MaxIterations = 1000 }, cancellationToken: TestContext.Current.CancellationToken);

        result.Errors.Should().Contain(e => e.Code == CalendarErrorCodes.RecurrenceLimit);
    }

    [Fact]
    public void RecurringTodo_EndsEachOccurrenceAtItsDue() {
        var todo = new CalendarTodo {
            Uid = "t", Start = CalendarDateTime.Utc(Utc(2026, 1, 1)), Due = CalendarDateTime.Utc(Utc(2026, 1, 3)),
            RecurrenceRules = { Rule("FREQ=WEEKLY;COUNT=3") },
        };

        var occurrences = CalendarRecurrence.Expand([todo], Utc(2026, 1, 1), Utc(2026, 2, 1), cancellationToken: TestContext.Current.CancellationToken).Occurrences;

        occurrences.Select(o => o.EndUtc).Should().Equal(Utc(2026, 1, 3), Utc(2026, 1, 10), Utc(2026, 1, 17));
        occurrences.Should().OnlyContain(o => o.EndKind == CalendarEndKind.Stated);
    }

    [Fact]
    public void EndKinds_SayWhereTheEndComesFrom() {
        CalendarEvent[] items = [
            new() { Uid = "d", Start = CalendarDateTime.Utc(Utc(2026, 1, 1)), Duration = TimeSpan.FromHours(1) },
            new() { Uid = "z", Start = CalendarDateTime.Utc(Utc(2026, 1, 1)) },
            new() { Uid = "a", Start = CalendarDateTime.FromDate(new DateOnly(2026, 1, 1)) },
        ];

        var occurrences = CalendarRecurrence.Expand(items, Utc(2025, 12, 31), Utc(2026, 1, 3), cancellationToken: TestContext.Current.CancellationToken).Occurrences;

        occurrences.ToDictionary(o => o.Item.Uid!, o => o.EndKind).Should().BeEquivalentTo(new Dictionary<string, CalendarEndKind> {
            ["d"] = CalendarEndKind.Duration, ["z"] = CalendarEndKind.DefaultZero, ["a"] = CalendarEndKind.DefaultDay,
        });
    }

    [Fact]
    public void ForeignRecurrenceScale_IsAnInvalidRule() {
        CalendarRecurrenceRule.TryParse("RSCALE=CHINESE;FREQ=YEARLY", out _).Should().BeFalse();
        CalendarRecurrenceRule.TryParse("RSCALE=GREGORIAN;FREQ=YEARLY", out _).Should().BeTrue();
    }
}
