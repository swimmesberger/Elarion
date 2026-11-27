using System.Text;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Calendars;
using Xunit;
using static Elarion.Tests.Calendars.CalendarTestFiles;

namespace Elarion.Tests.Calendars;

/// <summary>The scenarios "Import events with a preview", "Report errors" and "Files as real producers write them" of spec 0004.</summary>
public sealed class CalendarReadingTests {
    private static string[] WasteEvent(int number, int day) => Event(
        $"UID:waste-{number}@example.org",
        "DTSTAMP:20260101T000000Z",
        "SUMMARY:Restmüll",
        $"DTSTART;VALUE=DATE:202601{day:00}",
        $"DTEND;VALUE=DATE:202601{day + 1:00}");

    [Fact]
    public async Task UploadedFileWithAllDayEvents_ReadsEveryEventInFileOrder() {
        var result = await ReadAsync(Calendar(Enumerable.Range(1, 120).SelectMany(i => WasteEvent(i, i % 28 + 1)).ToArray()));

        result.Events.Should().HaveCount(120);
        result.HasErrors.Should().BeFalse();
        result.Events.Select(e => e.Index).Should().BeInAscendingOrder();
        var first = result.Events[0];
        first.HasValue.Should().BeTrue();
        first.Value!.Summary.Should().Be("Restmüll");
        first.Value.Start!.Value.Date.Should().Be(new DateOnly(2026, 1, 2));
        first.Value.End!.Value.Date.Should().Be(new DateOnly(2026, 1, 3));
        first.Value.IsAllDay.Should().BeTrue();
        first.LineNumber.Should().Be(4);
    }

    [Fact]
    public async Task EventWithAnInvalidStart_HasNoValueAndALocatedError_AndTheOthersRead() {
        var result = await ReadAsync(Calendar([
            .. Event("UID:a", "DTSTART:20260101T090000Z"),
            .. Event("UID:b", "DTSTART:20260102T090000Z"),
            .. Event("UID:c", "DTSTART:2026-13-45"),
            .. Event("UID:d", "DTSTART:20260104T090000Z"),
        ]));

        result.Events.Select(e => e.HasValue).Should().Equal(true, true, false, true);
        var error = result.Events[2].Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(CalendarErrorCodes.InvalidValue);
        error.Property.Should().Be("DTSTART");
        error.Uid.Should().Be("c");
        error.Value.Should().Be("2026-13-45");
        error.ExpectedKind.Should().Be("date-time");
        error.LineNumber.Should().Be(14);
        error.ItemIndex.Should().Be(3);
    }

    [Fact]
    public async Task InvalidPriority_KeepsTheValue_AndThePropertyInTheComponent() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART:20260101T090000Z", "PRIORITY:high")));

        var entry = result.Events.Should().ContainSingle().Subject;
        entry.HasValue.Should().BeTrue();
        entry.Value!.Priority.Should().BeNull();
        entry.Component.FindProperty("PRIORITY")!.RawValue.Should().Be("high");
        entry.Errors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.InvalidValue);
    }

    [Fact]
    public async Task ApplicationError_IsLocatedAndKeepsTheValue() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART:20200101T090000Z")));
        var entry = result.Events[0];

        entry.AddError("import.date_in_past", "The event lies in the past.", "DTSTART");

        entry.HasValue.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("import.date_in_past");
        error.LineNumber.Should().Be(6);
        error.ItemIndex.Should().Be(1);
        result.ToAppError("file").Fields()["file"].Should().ContainSingle().Which.Should().Contain("The event lies in the past.");
    }

    [Fact]
    public async Task EventsWithoutUid_ReadWithoutError() {
        var result = await ReadAsync(Calendar([.. Event("DTSTART:20260101T090000Z"), .. Event("DTSTART:20260102T090000Z")]));

        result.Events.Should().HaveCount(2).And.OnlyContain(e => e.HasValue);
        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task SameUidAndRecurrenceIdTwice_IsReportedOnTheLaterOne() {
        var result = await ReadAsync(Calendar([.. Event("UID:a", "DTSTART:20260101T090000Z"), .. Event("UID:a", "DTSTART:20260102T090000Z")]));

        result.Events[0].Errors.Should().BeEmpty();
        result.Events[1].Errors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.DuplicateUid);
        result.Events[1].HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task ToAppError_KeysByTheFieldPath_InEnglishByDefault_OrWithTheFormatter() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART:nope")));

        var error = result.ToAppError("file");
        error.Kind.Should().Be(ErrorKind.Validation);
        error.Fields()["file"].Should().ContainSingle().Which.Should().Contain("not a valid date-time");
        result.ToAppError("file", e => $"Fehler {e.Code}").Fields()["file"].Should().Equal("Fehler calendar.invalid_value");
    }

    [Fact]
    public async Task ManyErrors_AreCappedAt100Messages_AndOneCount() {
        var result = await ReadAsync(Calendar(Enumerable.Range(1, 250).SelectMany(i => Event($"UID:{i}", "DTSTART:broken")).ToArray()));

        var messages = result.ToAppError("file").Fields()["file"];
        messages.Should().HaveCount(101);
        messages[^1].Should().Be("150 more problems are not listed.");
    }

    [Fact]
    public async Task ByteOrderMarkAndUnfoldedLongLines_ReadAsIfNeither() {
        var summary = new string('x', 300);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Calendar(Event("UID:a", "DTSTART:20260101T090000Z", "SUMMARY:" + summary))))
            .ToArray();

        var result = await ReadAsync(bytes);

        result.Events[0].Value!.Summary.Should().Be(summary);
        result.ByteOrderMarkEncoding.Should().Be("utf-8");
    }

    [Fact]
    public async Task Windows1252File_ReadsWithTheFallback_AndSaysSo() {
        var text = Calendar(Event("UID:a", "DTSTART:20260101T090000Z", "SUMMARY:Müll"));
        var bytes = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes(text);

        var result = await ReadAsync(bytes);

        result.Events[0].Value!.Summary.Should().Be("Müll");
        result.UsedFallbackEncoding.Should().BeTrue();
    }

    [Fact]
    public async Task OneLegacyLineInAUtf8File_CostsOnlyThatLine() {
        var head = Encoding.UTF8.GetBytes(Calendar(Event("UID:a", "DTSTART:20260101T090000Z", "SUMMARY:Grüße", "LOCATION:@@@")));
        var text = Encoding.UTF8.GetString(head).Replace("@@@", "Straße", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(text);
        var index = IndexOf(bytes, Encoding.UTF8.GetBytes("Straße"));
        var legacy = bytes[..index].Concat("Stra"u8.ToArray()).Concat(new byte[] { 0xDF, (byte)'e' }).Concat(bytes[(index + 7)..]).ToArray();

        var result = await ReadAsync(legacy);

        result.Events[0].Value!.Summary.Should().Be("Grüße");
        result.Events[0].Value!.Location.Should().Be("Straße");
        result.UsedFallbackEncoding.Should().BeTrue();
    }

    [Fact]
    public async Task LfLineEndsAndTabFolds_ReadLikeCrlfAndSpaces() {
        var crlf = Calendar(Event("UID:a", "DTSTART:20260101T090000Z", "SUMMARY:Long", " er text"));
        var lf = crlf.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n er text", "\n\ter text", StringComparison.Ordinal);

        (await ReadAsync(lf)).Events[0].Value!.Summary.Should().Be("Longer text");
        (await ReadAsync(crlf)).Events[0].Value!.Summary.Should().Be("Longer text");
    }

    [Fact]
    public async Task FoldInsideAMultiByteCharacter_IsJoinedBeforeDecoding() {
        var bytes = Encoding.UTF8.GetBytes(Calendar(Event("UID:a", "DTSTART:20260101T090000Z", "SUMMARY:Ä")));
        var index = IndexOf(bytes, Encoding.UTF8.GetBytes("Ä"));
        var folded = bytes[..(index + 1)].Concat("\r\n "u8.ToArray()).Concat(bytes[(index + 1)..]).ToArray();

        (await ReadAsync(folded)).Events[0].Value!.Summary.Should().Be("Ä");
    }

    [Fact]
    public async Task MozillaTzidPrefix_ResolvesToTheIanaZone() {
        var berlin = TimeZones.Get("Europe/Berlin");

        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART;TZID=/mozilla.org/20070129_1/Europe/Berlin:20261201T090000")));

        var start = result.Events[0].Value!.Start!.Value;
        start.Kind.Should().Be(CalendarDateTimeKind.Zoned);
        start.TimeZone!.BaseUtcOffset.Should().Be(berlin.BaseUtcOffset);
        start.TimeZoneId.Should().Be("/mozilla.org/20070129_1/Europe/Berlin");
        start.ToUtc().Should().Be(new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task OutlookQuotedWindowsZone_StartsAtNineCentralEuropeanTime() {
        var result = await ReadAsync(Calendar([
            "BEGIN:VTIMEZONE", "TZID:W. Europe Standard Time",
            "BEGIN:STANDARD", "DTSTART:16010101T030000", "TZOFFSETFROM:+0200", "TZOFFSETTO:+0100",
            "RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=10", "END:STANDARD",
            "BEGIN:DAYLIGHT", "DTSTART:16010101T020000", "TZOFFSETFROM:+0100", "TZOFFSETTO:+0200",
            "RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=3", "END:DAYLIGHT", "END:VTIMEZONE",
            .. Event("UID:a", "DTSTART;TZID=\"W. Europe Standard Time\":20261201T090000"),
        ]));

        result.HasErrors.Should().BeFalse();
        result.Events[0].Value!.Start!.Value.ToUtc().Should().Be(new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task BrokenDateTimes_AreRepairedSilently() {
        var result = await ReadAsync(Calendar(Event("UID:a",
            "CREATED:20260101T000000ZT000000Z",
            "DTSTART;TZID=Europe/Vienna:20260505T",
            "DTSTAMP;VALUE=DATE:19760401",
            "DTEND;TZID=Europe/Vienna:20260505T1000")));

        result.HasErrors.Should().BeFalse();
        var item = result.Events[0].Value!;
        item.Created.Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        item.Start!.Value.Value.Should().Be(new DateTime(2026, 5, 5));
        item.Timestamp.Should().Be(new DateTimeOffset(1976, 4, 1, 0, 0, 0, TimeSpan.Zero));
        item.End!.Value.Value.Should().Be(new DateTime(2026, 5, 5, 10, 0, 0));
    }

    [Fact]
    public async Task OldExchangeAllDayFlag_MakesAnAllDayEvent() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART;TZID=Europe/Vienna:20261201T000000",
            "DTEND;TZID=Europe/Vienna:20261202T000000", "X-MICROSOFT-CDO-ALLDAYEVENT:TRUE")));

        var item = result.Events[0].Value!;
        item.IsAllDay.Should().BeTrue();
        item.Start!.Value.Date.Should().Be(new DateOnly(2026, 12, 1));
        item.End!.Value.Date.Should().Be(new DateOnly(2026, 12, 2));
    }

    [Fact]
    public async Task AllDayEventWithoutEnd_LastsOneDay() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART;VALUE=DATE:20261201")));

        var occurrence = result.Expand(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), cancellationToken: TestContext.Current.CancellationToken)
            .Occurrences.Should().ContainSingle().Subject;
        occurrence.End.Date.Should().Be(new DateOnly(2026, 12, 2));
        occurrence.EndKind.Should().Be(CalendarEndKind.DefaultDay);
    }

    [Fact]
    public async Task HtmlPage_IsUnreadable() {
        var result = await ReadAsync("<!DOCTYPE html><html><body>Not found</body></html>");

        result.FileErrors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.Unreadable);
        result.Events.Should().BeEmpty();
        result.Root.Should().BeNull();
    }

    [Fact]
    public async Task EventWithoutStart_IsRequired() {
        var result = await ReadAsync(Calendar(Event("UID:a", "SUMMARY:No time")));

        result.Events[0].HasValue.Should().BeFalse();
        result.Events[0].Errors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.Required);
    }

    [Fact]
    public async Task MalformedLine_IsSkippedWithAnError_AndTheItemKeepsItsValue() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART:20260101T090000Z", "this is not a content line")));

        result.Events[0].HasValue.Should().BeTrue();
        result.Events[0].Errors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.MalformedLine);
    }

    [Fact]
    public async Task FileEndingInsideAnEvent_KeepsTheEvent_WithOneUnexpectedEnd() {
        var text = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nDTSTART:20260101T090000Z\r\n";

        var result = await ReadAsync(text);

        result.Events.Should().ContainSingle().Which.HasValue.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.UnexpectedEnd);
    }

    [Fact]
    public async Task CalendarProperties_ReadFromTheStandardOrTheWidelyReadForm() {
        var result = await ReadAsync(Calendar("X-WR-CALNAME:Müllabfuhr", "X-PUBLISHED-TTL:PT12H", "X-WR-TIMEZONE:Europe/Berlin",
            "X-APPLE-CALENDAR-COLOR:#FF0000", "METHOD:PUBLISH"));

        result.Calendar.Name.Should().Be("Müllabfuhr");
        result.Calendar.RefreshInterval.Should().Be(TimeSpan.FromHours(12));
        result.Calendar.SuggestedTimeZone.Should().NotBeNull();
        result.Calendar.Color.Should().Be("#FF0000");
        result.Calendar.Method.Should().Be("PUBLISH");
        result.Calendar.ProductId.Should().Be("-//Test//Test//EN");
    }

    [Fact]
    public async Task ComponentTree_HoldsEveryPropertyAndParameter_WithLineNumbers() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART:20260101T090000Z",
            "X-APPLE-STRUCTURED-LOCATION;VALUE=URI;X-TITLE=\"Main St, 1\":geo:48.2,16.3")));

        var property = result.Events[0].Component.FindProperty("X-APPLE-STRUCTURED-LOCATION")!;
        property.GetParameter("X-TITLE").Should().Be("Main St, 1");
        property.RawValue.Should().Be("geo:48.2,16.3");
        property.LineNumber.Should().Be(7);
        result.Root!.Components.Should().ContainSingle();
    }

    [Fact]
    public async Task TodoWithDueStatusAndCompletion_ReadsEachProperty() {
        var result = await ReadAsync(Calendar("BEGIN:VTODO", "UID:t", "DTSTAMP:20260101T000000Z", "DUE:20260201T120000Z", "STATUS:COMPLETED",
            "PERCENT-COMPLETE:100", "COMPLETED:20260115T080000Z", "END:VTODO"));

        var todo = result.Todos.Should().ContainSingle().Subject.Value!;
        todo.Due!.Value.Value.Should().Be(new DateTime(2026, 2, 1, 12, 0, 0));
        todo.Status.Should().Be("COMPLETED");
        todo.PercentComplete.Should().Be(100);
        todo.Completed.Should().Be(new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task AppleAlarmWithActionNone_IsAnUnknownAlarm_WithoutTriggerTimes() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART:20260101T090000Z", "BEGIN:VALARM", "ACTION:NONE",
            "TRIGGER;VALUE=DATE-TIME:19760401T005545Z", "END:VALARM")));

        var item = result.Events[0].Value!;
        var alarm = item.Alarms.Should().ContainSingle().Subject;
        alarm.Action.Should().Be(CalendarAlarmAction.Unknown);
        var occurrence = result.Expand(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), cancellationToken: TestContext.Current.CancellationToken)
            .Occurrences.Single();
        alarm.GetTriggerTimes(occurrence).Should().BeEmpty();
    }

    [Fact]
    public async Task RepeatingAlarm_ResolvesItsThreeTriggers() {
        var result = await ReadAsync(Calendar(Event("UID:a", "DTSTART:20260101T090000Z", "BEGIN:VALARM", "ACTION:DISPLAY",
            "DESCRIPTION:Reminder", "TRIGGER:-PT15M", "REPEAT:2", "DURATION:PT5M", "END:VALARM")));

        var occurrence = result.Expand(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), cancellationToken: TestContext.Current.CancellationToken)
            .Occurrences.Single();
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        result.Events[0].Value!.Alarms[0].GetTriggerTimes(occurrence)
            .Should().Equal(start.AddMinutes(-15), start.AddMinutes(-10), start.AddMinutes(-5));
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);
}

internal static class AppErrorFields {
    public static IReadOnlyDictionary<string, string[]> Fields(this AppError error) => ((ValidationErrorData)error.Data!).FieldErrors!;
}
