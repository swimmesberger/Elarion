using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Calendars;
using Xunit;
using static Elarion.Tests.Calendars.CalendarRecurrenceRfcTests;
using static Elarion.Tests.Calendars.CalendarTestFiles;

namespace Elarion.Tests.Calendars;

/// <summary>
/// The scenarios "Hostile files are bounded" of spec 0004 for time zones, expansion work and file limits, and the
/// override, EXRULE and strict-read rules of ADR-0109's amendment of 2026-11-27.
/// </summary>
public sealed class CalendarHostileInputTests {
    private static readonly DateTimeOffset NewYear = Utc(2026, 1, 1);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- time zones ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task VendorPrefixWithThousandsOfSegments_ResolvesInLinearTime() {
        TimeZones.Get("Europe/Vienna");
        // 64 KB of segments took 3.1 s before the suffixes were sliced from the end; now it takes under a millisecond.
        var tzid = string.Concat(Enumerable.Repeat("/a", 32_000)) + "/Europe/Vienna";
        var text = Calendar(Event("UID:a", $"DTSTART;TZID={tzid}:20260701T090000"));

        var stopwatch = Stopwatch.StartNew();
        var result = await ReadAsync(text, new CalendarReadOptions { MaxTimeZoneIdLength = int.MaxValue });

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        result.Events[0].Value!.Start!.Value.ToUtc().Should().Be(new DateTimeOffset(2026, 7, 1, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task TzidLongerThanTheLimit_IsUnknown_EvenWhenItWouldResolve() {
        TimeZones.Get("Europe/Vienna");
        var prefix = "/" + new string('x', CalendarReadOptions.DefaultMaxTimeZoneIdLength - "/Europe/Vienna".Length - 1);
        var within = prefix + "/Europe/Vienna";
        var beyond = "/x" + within;

        var result = await ReadAsync(Calendar([
            .. Event("UID:a", $"DTSTART;TZID={within}:20260701T090000"),
            .. Event("UID:b", $"DTSTART;TZID={beyond}:20260701T090000"),
        ]));

        within.Length.Should().Be(CalendarReadOptions.DefaultMaxTimeZoneIdLength);
        result.Events[0].HasValue.Should().BeTrue();
        result.Events[1].HasValue.Should().BeFalse();
        result.Events[1].Errors.Should().ContainSingle().Which.Should().Match<CalendarError>(e =>
            e.Code == CalendarErrorCodes.UnknownTimeZone && e.TimeZoneId == beyond);
    }

    [Fact]
    public async Task VTimeZoneWithAnIdLongerThanTheLimit_IsNotUsed() {
        var tzid = "Custom " + new string('x', 200);
        var result = await ReadAsync(Calendar([
            "BEGIN:VTIMEZONE", $"TZID:{tzid}", "BEGIN:STANDARD", "DTSTART:19700101T000000", "TZOFFSETFROM:+0100", "TZOFFSETTO:+0100",
            "END:STANDARD", "END:VTIMEZONE",
            .. Event("UID:a", $"DTSTART;TZID={tzid}:20260701T090000"),
        ]));

        result.Events[0].Errors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.UnknownTimeZone);
        (await ReadAsync(Calendar([
            "BEGIN:VTIMEZONE", $"TZID:{tzid}", "BEGIN:STANDARD", "DTSTART:19700101T000000", "TZOFFSETFROM:+0100", "TZOFFSETTO:+0100",
            "END:STANDARD", "END:VTIMEZONE",
            .. Event("UID:a", $"DTSTART;TZID={tzid}:20260701T090000"),
        ]), new CalendarReadOptions { MaxTimeZoneIdLength = 300 })).Events[0].HasValue.Should().BeTrue();
    }

    [Fact]
    public void DistinctUnknownIds_LeaveNothingInTheProcessWideCaches() {
        var before = CalendarTimeZones.CachedEntryCount;

        for (var i = 0; i < 2_000; i++) CalendarTimeZones.TryResolve($"/vendor/{i}/Nowhere/Zone{i}", out _).Should().BeFalse();

        // 200 reads of a distinct 100 KB id each kept 39 MB before misses stopped being cached; now they keep nothing.
        // Other tests resolve real zones meanwhile, which the caches may add.
        (CalendarTimeZones.CachedEntryCount - before).Should().BeLessThan(100);
    }

    [Fact]
    public async Task SuggestedTimeZoneLongerThanTheLimit_IsKeptAsAProperty() {
        var result = await ReadAsync(Calendar("X-WR-TIMEZONE:/" + new string('x', 200) + "/Europe/Vienna"));

        result.Calendar.SuggestedTimeZone.Should().BeNull();
        result.Calendar.AdditionalProperties.Should().ContainSingle(p => p.Name == "X-WR-TIMEZONE");
    }

    // ---- expansion work -----------------------------------------------------------------------------------------

    [Fact]
    public async Task EverySecondOfAYear_StopsAtTheCandidateCap_InsteadOfBuildingThemAll() {
        static string All(int from, int to) => string.Join(',', Enumerable.Range(from, to - from + 1));
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART:20260101T000000Z",
            $"RRULE:FREQ=YEARLY;BYMONTH={All(1, 12)};BYMONTHDAY={All(1, 31)};BYHOUR={All(0, 23)};BYMINUTE={All(0, 59)};BYSECOND={All(0, 59)}")));

        // Before the cap applied within a period: 582 ms and 2 GB allocated for the 31.5 million candidates of one year.
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var occurrences = result.Expand(NewYear, NewYear.AddMinutes(1), cancellationToken: Ct);

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().BeLessThan(256L * 1024 * 1024);
        occurrences.Errors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.RecurrenceLimit);
    }

    [Theory]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=1,15", 2)]
    [InlineData("FREQ=YEARLY;BYMONTH=1;BYDAY=MO", 4)]
    public async Task RuleFromADistantStart_SkipsAheadToTheWindow(string rule, int expected) {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART;VALUE=DATE:16010101", "RRULE:" + rule)));

        // Enumerated from 1601, the monthly rule builds 10,000 and the yearly one 22,000 candidates.
        var occurrences = result.Expand(NewYear, Utc(2026, 2, 1), new CalendarExpandOptions { MaxCandidates = 1_000 }, Ct);

        occurrences.Errors.Should().BeEmpty();
        occurrences.Occurrences.Should().HaveCount(expected);
    }

    [Fact]
    public async Task MonthlyRuleOfEveryMinute_FromADistantStart_FindsTheWindowsOccurrences() {
        var minutes = string.Join(',', Enumerable.Range(0, 60));
        var hours = string.Join(',', Enumerable.Range(0, 24));
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART:19000101T000000Z", $"RRULE:FREQ=MONTHLY;BYHOUR={hours};BYMINUTE={minutes}")));

        // Before monthly rules skipped ahead: 134 ms and no occurrence, the series stopped after a million candidates.
        var occurrences = result.Expand(NewYear, NewYear.AddHours(1), cancellationToken: Ct);

        occurrences.Errors.Should().BeEmpty();
        occurrences.Occurrences.Should().HaveCount(60);
    }

    [Fact]
    public async Task CountedRuleFromADistantStart_StopsAtTheCandidateCap() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART:19000101T000000Z", "RRULE:FREQ=DAILY;COUNT=100000000")));

        var occurrences = result.Expand(NewYear, Utc(2026, 1, 2), new CalendarExpandOptions { MaxCandidates = 10_000 }, Ct);

        occurrences.Errors.Should().ContainSingle().Which.Should().Match<CalendarError>(e =>
            e.Code == CalendarErrorCodes.RecurrenceLimit && e.Uid == "a" && e.Property == "RRULE");
    }

    [Fact]
    public async Task CancelledToken_StopsTheExpansion() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART:20260101T000000Z", "RRULE:FREQ=DAILY")));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var expand = () => result.Expand(NewYear, Utc(2027, 1, 1), cancellationToken: source.Token);

        expand.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void RepeatedByValues_AreKeptOnce_AndALongerListThanThePartsValuesDoesNotParse() {
        CalendarRecurrenceRule.TryParse("FREQ=DAILY;BYMONTH=1,1,2,1;BYDAY=MO,MO,1MO", out var rule).Should().BeTrue();
        rule!.ByMonth.Should().Equal(1, 2);
        rule.ByDay.Should().Equal(new CalendarWeekday(DayOfWeek.Monday), new CalendarWeekday(DayOfWeek.Monday, 1));

        CalendarRecurrenceRule.TryParse("FREQ=DAILY;BYMONTH=" + string.Join(',', Enumerable.Repeat("1", 13)), out _).Should().BeFalse();
        CalendarRecurrenceRule.TryParse("FREQ=DAILY;BYHOUR=" + string.Join(',', Enumerable.Range(0, 24)), out _).Should().BeTrue();
    }

    // ---- file limits --------------------------------------------------------------------------------------------

    [Fact]
    public async Task MoreLinesThanTheLimit_StopsTheRead_KeepingTheItemsBefore() {
        var text = Calendar(Enumerable.Range(1, 10).SelectMany(i => Event($"UID:{i}", "DTSTART:20260101T090000Z")).ToArray());

        // Three header lines and four lines per event: the limit falls inside the fourth event.
        var result = await ReadAsync(text, new CalendarReadOptions { MaxLines = 18 });

        result.Events.Should().HaveCount(3);
        result.FileErrors.Should().ContainSingle().Which.Should().Match<CalendarError>(e => e.Code == CalendarErrorCodes.TooManyLines && e.Limit == 18);
    }

    [Fact]
    public async Task MoreItemsThanTheLimit_StopsTheRead_AlarmsAndTimeZonesNotCounted() {
        var text = Calendar(Enumerable.Range(1, 10)
            .SelectMany(i => Event($"UID:{i}", "DTSTART:20260101T090000Z", "BEGIN:VALARM", "TRIGGER:-PT5M", "END:VALARM")).ToArray());

        var result = await ReadAsync(text, new CalendarReadOptions { MaxItems = 4 });

        result.Events.Should().HaveCount(4);
        result.Events.Should().OnlyContain(e => e.HasValue && e.Value!.Alarms.Count == 1);
        result.FileErrors.Should().ContainSingle().Which.Should().Match<CalendarError>(e => e.Code == CalendarErrorCodes.TooManyItems && e.Limit == 4);
    }

    // ---- overrides ----------------------------------------------------------------------------------------------

    private static string[] WeeklySeries(params string[][] overrides) => [
        .. Event("UID:s", "DTSTART:20260105T090000Z", "RRULE:FREQ=WEEKLY;COUNT=3", "SUMMARY:Master"),
        .. overrides.SelectMany(static o => o),
    ];

    private static string[] Override(string summary, int sequence, string stamp, string? status = null) =>
        Event([
            "UID:s", "RECURRENCE-ID:20260112T090000Z", "DTSTART:20260112T100000Z", $"SUMMARY:{summary}", $"SEQUENCE:{sequence}",
            $"DTSTAMP:{stamp}", .. status is null ? Array.Empty<string>() : [$"STATUS:{status}"],
        ]);

    [Fact]
    public async Task OverridesOfOneInstance_TheHighestSequenceCounts() {
        var result = await ReadAsync(Calendar(WeeklySeries(Override("v2", 2, "20260101T000000Z"), Override("v1", 1, "20260102T000000Z"))));

        var occurrences = result.Expand(NewYear, Utc(2026, 2, 1), cancellationToken: Ct).Occurrences;

        occurrences.Select(o => o.Item.Summary).Should().Equal("Master", "v2", "Master");
        result.Expand(NewYear, Utc(2026, 2, 1), new CalendarExpandOptions { ResolveOverrideVersions = false }, Ct).Occurrences
            .Should().HaveCount(4);
    }

    [Fact]
    public async Task OverridesOfOneInstance_WithOneSequence_TheLatestStampCounts_ThenTheLaterOne() {
        var byStamp = await ReadAsync(Calendar(WeeklySeries(Override("later", 1, "20260102T000000Z"), Override("earlier", 1, "20260101T000000Z"))));
        var byOrder = await ReadAsync(Calendar(WeeklySeries(Override("first", 1, "20260101T000000Z"), Override("second", 1, "20260101T000000Z"))));

        byStamp.Expand(NewYear, Utc(2026, 2, 1), cancellationToken: Ct).Occurrences[1].Item.Summary.Should().Be("later");
        byOrder.Expand(NewYear, Utc(2026, 2, 1), cancellationToken: Ct).Occurrences[1].Item.Summary.Should().Be("second");
    }

    [Fact]
    public async Task CancelledOverride_RemovesItsInstance_WhenCancelledItemsAreSkipped() {
        var result = await ReadAsync(Calendar(WeeklySeries(Override("cancelled", 1, "20260101T000000Z", "CANCELLED"))));

        var shown = result.Expand(NewYear, Utc(2026, 2, 1), new CalendarExpandOptions { SkipCancelled = true }, Ct).Occurrences;
        var stated = result.Expand(NewYear, Utc(2026, 2, 1), cancellationToken: Ct).Occurrences;

        shown.Select(o => o.Start.Value.Day).Should().Equal(5, 19);
        stated.Select(o => o.Item.Summary).Should().Equal("Master", "cancelled", "Master");
    }

    [Fact]
    public async Task CancelledMaster_RemovesItsSeries_WhenCancelledItemsAreSkipped() {
        var result = await ReadAsync(Calendar([
            .. Event("UID:s", "DTSTART:20260105T090000Z", "RRULE:FREQ=WEEKLY;COUNT=3", "STATUS:CANCELLED"),
            .. Override("moved", 1, "20260101T000000Z"),
            .. Event("UID:other", "DTSTART:20260106T090000Z"),
        ]));

        var occurrences = result.Expand(NewYear, Utc(2026, 2, 1), new CalendarExpandOptions { SkipCancelled = true }, Ct).Occurrences;

        occurrences.Should().ContainSingle().Which.Item.Uid.Should().Be("other");
    }

    // ---- EXRULE -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExceptionRule_ExcludesItsInstances_ButNotTheStart_AndIsWrittenBack() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTAMP:20260101T000000Z", "DTSTART:20260105T090000Z",
            "RRULE:FREQ=WEEKLY;COUNT=6", "EXRULE:FREQ=WEEKLY;INTERVAL=2")));

        var item = result.Events[0].Value!;
        var occurrences = result.Expand(NewYear, Utc(2026, 3, 1), cancellationToken: Ct).Occurrences;

        item.ExceptionRules.Should().ContainSingle().Which.Interval.Should().Be(2);
        item.AdditionalProperties.Should().BeEmpty();
        occurrences.Select(o => o.Start.Value.Day).Should().Equal(5, 12, 26, 9);
        (await WriteAsync([item])).Should().Contain("EXRULE:FREQ=WEEKLY;INTERVAL=2\r\n");
    }

    [Fact]
    public async Task UnreadableExceptionRule_LeavesTheItemWithoutValue() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART:20260105T090000Z", "RRULE:FREQ=WEEKLY", "EXRULE:FREQ=FORTNIGHTLY")));

        result.Events[0].HasValue.Should().BeFalse();
        result.Events[0].Errors.Should().ContainSingle().Which.Should().Match<CalendarError>(e =>
            e.Code == CalendarErrorCodes.InvalidValue && e.Property == "EXRULE");
    }

    // ---- strict reads -------------------------------------------------------------------------------------------

    [Fact]
    public async Task CountWithUntil_IsAcceptedLeniently_AndRefusedStrictly() {
        var text = Calendar(Event("UID:a", "DTSTART:20260105T090000Z", "RRULE:FREQ=WEEKLY;COUNT=3;UNTIL=20260301T000000Z"));

        var lenient = await ReadAsync(text);
        var strict = await ReadAsync(text, new CalendarReadOptions { Strict = true });

        lenient.Events[0].HasValue.Should().BeTrue();
        strict.Events[0].HasValue.Should().BeFalse();
        strict.Events[0].Errors.Should().ContainSingle().Which.Should().Match<CalendarError>(e =>
            e.Code == CalendarErrorCodes.InvalidValue && e.Property == "RRULE");
    }

    [Fact]
    public async Task ControlCharacter_IsKeptLeniently_AndSkippedStrictly_AsAStructuralError() {
        var text = Calendar(Event("UID:a", "DTSTART:20260105T090000Z", "SUMMARY:Bell\u0007", "DESCRIPTION:Tab\tis fine"));

        var lenient = await ReadAsync(text);
        var strict = await ReadAsync(text, new CalendarReadOptions { Strict = true });

        lenient.Events[0].Value!.Summary.Should().Be("Bell\u0007");
        strict.Events[0].Value!.Summary.Should().BeNull();
        strict.Events[0].Value!.Description.Should().Be("Tab\tis fine");
        var error = strict.Events[0].Errors.Should().ContainSingle().Subject;
        error.Should().Match<CalendarError>(e => e.Code == CalendarErrorCodes.ControlCharacter && e.Property == "SUMMARY");
        CalendarErrorCodes.IsStructural(error.Code).Should().BeTrue();
        CalendarErrorCodes.IsStructural(CalendarErrorCodes.UnknownTimeZone).Should().BeFalse();
    }
}
