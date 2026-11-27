using System.Text;
using AwesomeAssertions;
using Elarion.Calendars;
using Xunit;
using static Elarion.Tests.Calendars.CalendarRecurrenceRfcTests;
using static Elarion.Tests.Calendars.CalendarTestFiles;

namespace Elarion.Tests.Calendars;

/// <summary>The scenarios "Publish a calendar or a feed", "Properties of other programs survive" and "Work on the component tree" of spec 0004.</summary>
public sealed class CalendarWritingTests {
    private static readonly DateTimeOffset Stamp = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static CalendarEvent[] SampleEvents() {
        var vienna = TimeZones.Get("Europe/Vienna");
        return [
            new CalendarEvent {
                Uid = "a@example.com", Summary = "Waste collection", Start = CalendarDateTime.FromDate(new DateOnly(2026, 12, 1)),
                End = CalendarDateTime.FromDate(new DateOnly(2026, 12, 2)),
            },
            new CalendarEvent {
                Uid = "b@example.com", Summary = "Team meeting", Location = "Room 1",
                Start = CalendarDateTime.Zoned(new DateTime(2026, 12, 3, 9, 0, 0), vienna),
                End = CalendarDateTime.Zoned(new DateTime(2026, 12, 3, 10, 0, 0), vienna),
                RecurrenceRules = { Rule("FREQ=WEEKLY;COUNT=5") },
            },
        ];
    }

    [Fact]
    public async Task WrittenCalendar_IsUtf8Crlf_WithATimeZonePerZone_AndReadsBackEqual() {
        var events = SampleEvents();

        var text = await WriteAsync(events, new CalendarHeader { Name = "Shifts" }, new CalendarWriteOptions { Timestamp = Stamp });

        text.Should().StartWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n");
        text.Should().EndWith("END:VCALENDAR\r\n");
        text.Replace("\r\n", "", StringComparison.Ordinal).Should().NotContain("\n");
        text.Should().Contain("NAME:Shifts\r\nX-WR-CALNAME:Shifts");
        Count(text, "BEGIN:VTIMEZONE").Should().Be(1);
        text.IndexOf("TZID:Europe/Vienna", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("UID:b@example.com", StringComparison.Ordinal));

        var result = await ReadAsync(text);
        result.HasErrors.Should().BeFalse();
        result.Events.Select(e => e.Value!.Uid).Should().Equal("a@example.com", "b@example.com");
        result.Events[1].Value!.Start.Should().Be(events[1].Start);
        result.Events[1].Value!.RecurrenceRules.Should().Equal(events[1].RecurrenceRules);
        result.Events[1].Value!.Timestamp.Should().Be(Stamp);
    }

    [Fact]
    public async Task SameInputAndTimestamp_WritesTheSameBytes() {
        var first = await WriteAsync(SampleEvents(), options: new CalendarWriteOptions { Timestamp = Stamp });
        var second = await WriteAsync(SampleEvents(), options: new CalendarWriteOptions { Timestamp = Stamp });

        second.Should().Be(first);
    }

    [Fact]
    public async Task EventWithoutTimestamp_FailsNamingTheEvent() {
        var act = () => WriteAsync([new CalendarEvent { Uid = "x", Start = CalendarDateTime.Utc(Stamp) }]);

        (await act.Should().ThrowAsync<CalendarWriteException>()).Which.Should().Match<CalendarWriteException>(e =>
            e.ItemIndex == 1 && e.Uid == "x" && e.Property == "DTSTAMP");
    }

    [Fact]
    public async Task EventWithoutUid_OrEndingBeforeItStarts_Fails() {
        var noUid = () => WriteAsync([new CalendarEvent { Timestamp = Stamp, Start = CalendarDateTime.Utc(Stamp) }]);
        var backwards = () => WriteAsync([
            new CalendarEvent { Uid = "x", Timestamp = Stamp, Start = CalendarDateTime.Utc(Stamp), End = CalendarDateTime.Utc(Stamp.AddHours(-1)) },
        ]);

        (await noUid.Should().ThrowAsync<CalendarWriteException>()).Which.Property.Should().Be("UID");
        (await backwards.Should().ThrowAsync<CalendarWriteException>()).Which.Property.Should().Be("DTEND");
    }

    [Fact]
    public async Task TwoZones_EachGetOneTimeZoneBeforeTheirFirstUse() {
        var vienna = TimeZones.Get("Europe/Vienna");
        var newYork = TimeZones.Get("America/New_York");
        CalendarEvent[] events = [
            new() { Uid = "1", Timestamp = Stamp, Start = CalendarDateTime.Zoned(new DateTime(2026, 7, 1, 9, 0, 0), vienna) },
            new() { Uid = "2", Timestamp = Stamp, Start = CalendarDateTime.Zoned(new DateTime(2026, 7, 1, 9, 0, 0), newYork) },
            new() { Uid = "3", Timestamp = Stamp, Start = CalendarDateTime.Zoned(new DateTime(2026, 12, 1, 9, 0, 0), vienna) },
        ];

        var text = await WriteAsync(events);

        Count(text, "BEGIN:VTIMEZONE").Should().Be(2);
        text.IndexOf("TZID:America/New_York", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("UID:2", StringComparison.Ordinal))
            .And.BeGreaterThan(text.IndexOf("UID:1", StringComparison.Ordinal));
        var read = await ReadAsync(text);
        read.Events.Select(e => e.Value!.Start!.Value.ToUtc()).Should().Equal(
            new DateTimeOffset(2026, 7, 1, 7, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 7, 1, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task IncludeTimeZonesFalse_LeavesOutZonesWithAnIanaId() {
        var vienna = TimeZones.Get("Europe/Vienna");

        var text = await WriteAsync([new CalendarEvent { Uid = "1", Timestamp = Stamp, Start = CalendarDateTime.Zoned(new DateTime(2026, 7, 1, 9, 0, 0), vienna) }],
            options: new CalendarWriteOptions { IncludeTimeZones = false });

        text.Should().NotContain("BEGIN:VTIMEZONE");
    }

    [Fact]
    public async Task SummaryWithDelimitersAndNonAscii_RoundTrips_AndNoLineExceeds75Octets() {
        var summary = "Meeting; with, commas\nand a line break — Grüße aus Wien, äöü ß " + new string('é', 60);

        var text = await WriteAsync([new CalendarEvent { Uid = "1", Timestamp = Stamp, Start = CalendarDateTime.Utc(Stamp), Summary = summary }]);

        foreach (var line in text.Split("\r\n")) Encoding.UTF8.GetByteCount(line).Should().BeLessThanOrEqualTo(75);
        (await ReadAsync(text)).Events[0].Value!.Summary.Should().Be(summary);
    }

    [Fact]
    public async Task NameAndRefreshInterval_AreWrittenInBothForms() {
        var text = await WriteAsync([], new CalendarHeader { Name = "Feed", RefreshInterval = TimeSpan.FromHours(6) });

        text.Should().Contain("NAME:Feed").And.Contain("X-WR-CALNAME:Feed")
            .And.Contain("REFRESH-INTERVAL;VALUE=DURATION:PT6H").And.Contain("X-PUBLISHED-TTL:PT6H");
    }

    [Fact]
    public async Task OtherProgramsProperties_SurviveAReadAndAWrite() {
        var source = Calendar(Event("UID:a", "DTSTAMP:20260101T000000Z", "DTSTART:20260101T090000Z",
            "X-APPLE-STRUCTURED-LOCATION;VALUE=URI;X-ADDRESS=\"Main St, 1\";X-TITLE=Home:geo:48.2,16.3",
            "BEGIN:X-UNKNOWN", "X-FOO:bar", "END:X-UNKNOWN"));
        var read = await ReadAsync(source);

        var written = (await WriteAsync(read.Items)).Replace("\r\n ", "", StringComparison.Ordinal);

        written.Should().Contain("X-APPLE-STRUCTURED-LOCATION;VALUE=URI;X-ADDRESS=\"Main St, 1\";X-TITLE=Home:geo:48.2,16.3");
        written.Should().Contain("BEGIN:X-UNKNOWN\r\nX-FOO:bar\r\nEND:X-UNKNOWN");
    }

    [Fact]
    public async Task ComponentTree_IsWrittenInTheOrderItHolds() {
        var read = await ReadAsync(Calendar(Event("UID:a", "DTSTART:20260101T090000Z", "SUMMARY:One", "DESCRIPTION:Drop me")));
        var root = read.Root!.Clone();
        var item = root.Components[0];
        item.Properties.Remove(item.FindProperty("DESCRIPTION")!);

        using var stream = new MemoryStream();
        await CalendarWriter.WriteAsync(stream, root, cancellationToken: TestContext.Current.CancellationToken);
        var text = Encoding.UTF8.GetString(stream.ToArray());

        text.Should().Be(Calendar(Event("UID:a", "DTSTART:20260101T090000Z", "SUMMARY:One")));
    }

    [Fact]
    public void TypedEvent_ToComponentAndBack_IsEqual() {
        var source = SampleEvents()[1];
        source.Timestamp = Stamp;
        source.Attendees.Add(new CalendarAttendee { Address = "mailto:a@example.com", CommonName = "A", Rsvp = true });
        source.Alarms.Add(new CalendarAlarm { Action = CalendarAlarmAction.Display, Trigger = TimeSpan.FromMinutes(-15), Description = "Soon" });
        source.Categories.Add("Work");

        CalendarEvent.TryFromComponent(source.ToComponent(), out var back, out var errors).Should().BeTrue();

        errors.Should().BeEmpty();
        back.Should().BeEquivalentTo(source, options => options.Excluding(e => e.Start).Excluding(e => e.End));
        back!.Start.Should().Be(source.Start);
        back.End.Should().Be(source.End);
    }

    [Fact]
    public async Task ToFileAsync_ServesTextCalendar() {
        var file = await CalendarWriter.ToFileAsync(SampleEvents(), "shifts.ics", options: new CalendarWriteOptions { Timestamp = Stamp },
            cancellationToken: TestContext.Current.CancellationToken);

        file.ContentType.Should().Be("text/calendar; charset=utf-8");
        file.FileName.Should().Be("shifts.ics");
    }

    [Fact]
    public async Task StreamedFile_SendsTheFirstBytesBeforeTheLastEventIsProduced() {
        var produced = 0;
        var sawBytesBeforeEnd = false;
        var body = new ObservingStream(() => sawBytesBeforeEnd |= produced < 100_000);
        var file = CalendarWriter.ToStreamedFile(Produce(), "big.ics", options: new CalendarWriteOptions { Timestamp = Stamp });

        await using (file) await file.WriteToAsync(body, TestContext.Current.CancellationToken);

        sawBytesBeforeEnd.Should().BeTrue();
        produced.Should().Be(100_000);

        async IAsyncEnumerable<CalendarItem> Produce() {
            for (var i = 0; i < 100_000; i++) {
                produced++;
                yield return new CalendarEvent { Uid = $"e{i}", Start = CalendarDateTime.Utc(Stamp.AddHours(i)), Summary = "Event" };
            }

            await Task.CompletedTask;
        }
    }

    private static int Count(string text, string value) {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    private sealed class ObservingStream(Action onWrite) : MemoryStream {
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
            onWrite();
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
            onWrite();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
