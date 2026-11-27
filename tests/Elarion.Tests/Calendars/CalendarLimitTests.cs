using System.Text;
using AwesomeAssertions;
using Elarion.Calendars;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using static Elarion.Tests.Calendars.CalendarTestFiles;

namespace Elarion.Tests.Calendars;

/// <summary>The scenarios "Hostile files are bounded" of spec 0004, and the registration.</summary>
public sealed class CalendarLimitTests {
    [Fact]
    public async Task FileLargerThanTheLimit_IsOneTooLargeError_WithoutReadingFurther() {
        var stream = new CountingStream(new byte[10_000]);

        var result = await new CalendarReader().ReadAsync(stream, new CalendarReadOptions { MaxBytes = 1_000 }, TestContext.Current.CancellationToken);

        result.FileErrors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.TooLarge);
        stream.BytesRead.Should().BeLessThanOrEqualTo(1_001);
    }

    [Fact]
    public async Task MoreComponentsThanTheLimit_StopsTheRead_KeepingTheItemsBefore() {
        var text = Calendar(Enumerable.Range(1, 20).SelectMany(i => Event($"UID:{i}", "DTSTART:20260101T090000Z")).ToArray());

        var result = await ReadAsync(text, new CalendarReadOptions { MaxComponents = 11 });

        result.Events.Should().HaveCount(10);
        result.FileErrors.Should().ContainSingle().Which.Should().Match<CalendarError>(e => e.Code == CalendarErrorCodes.TooManyComponents && e.Limit == 11);
    }

    [Fact]
    public async Task LineLongerThanTheLimit_IsDropped_AndTheEventsAfterItRead() {
        var text = Calendar([
            .. Event("UID:a", "DTSTART:20260101T090000Z", "DESCRIPTION:" + new string('x', 5_000)),
            .. Event("UID:b", "DTSTART:20260102T090000Z"),
        ]);

        var result = await ReadAsync(text, new CalendarReadOptions { MaxLineBytes = 1_000 });

        result.Events[0].Value!.Description.Should().BeNull();
        result.Events[0].Errors.Should().ContainSingle().Which.Should().Match<CalendarError>(e =>
            e.Code == CalendarErrorCodes.LineTooLong && e.Property == "DESCRIPTION" && e.Limit == 1_000);
        result.Events[1].HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task EventWithTooManyProperties_HasNoValue_AndTheNextReads() {
        var text = Calendar([
            .. Event(["UID:a", "DTSTART:20260101T090000Z", .. Enumerable.Range(1, 50).Select(i => $"X-P{i}:v"), "BEGIN:VALARM", "END:VALARM"]),
            .. Event("UID:b", "DTSTART:20260102T090000Z"),
        ]);

        var result = await ReadAsync(text, new CalendarReadOptions { MaxPropertiesPerComponent = 20 });

        result.Events.Should().HaveCount(2);
        result.Events[0].HasValue.Should().BeFalse();
        result.Events[0].Errors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.TooManyProperties);
        result.Events[1].HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task EventNestedTooDeep_HasNoValue_AndTheNextReads() {
        var nesting = Enumerable.Range(1, 12).Select(i => $"BEGIN:X-L{i}").Concat(Enumerable.Range(1, 12).Reverse().Select(i => $"END:X-L{i}"));
        var text = Calendar([.. Event(["UID:a", "DTSTART:20260101T090000Z", .. nesting]), .. Event("UID:b", "DTSTART:20260102T090000Z")]);

        var result = await ReadAsync(text);

        result.Events.Should().HaveCount(2);
        result.Events[0].Errors.Should().ContainSingle().Which.Code.Should().Be(CalendarErrorCodes.NestingTooDeep);
        result.Events[0].HasValue.Should().BeFalse();
        result.Events[1].HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task RandomBytes_NeverThrow() {
        var random = new Random(4711);
        for (var i = 0; i < 200; i++) {
            var bytes = new byte[random.Next(0, 2_000)];
            random.NextBytes(bytes);
            var prefixed = Encoding.ASCII.GetBytes("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\n").Concat(bytes).ToArray();

            await ReadAsync(prefixed);
            await ReadAsync(bytes);
        }
    }

    [Fact]
    public void Registration_BindsTheSection_AndRegistersOneReader() {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Calendars:ReadDefaults:MaxBytes"] = "1234",
            ["Calendars:ExpandDefaults:MaxOccurrences"] = "50",
            ["Calendars:ExpandDefaults:FloatingTimeZone"] = "UTC",
        }).Build();
        var services = new ServiceCollection().AddElarionCalendars(configuration.GetSection(CalendarOptions.SectionName)).AddElarionCalendars()
            .BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<CalendarOptions>>().Value;
        options.ReadDefaults.MaxBytes.Should().Be(1234);
        options.ExpandDefaults.MaxOccurrences.Should().Be(50);
        options.ExpandDefaults.FloatingTimeZone.Should().Be(TimeZoneInfo.Utc);
        services.GetServices<CalendarReader>().Should().ContainSingle();
    }

    [Fact]
    public async Task UnknownFallbackEncoding_IsAMisconfiguration() {
        var act = () => ReadAsync("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", new CalendarReadOptions { FallbackEncoding = "x-nothing" });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class CountingStream(byte[] data) : Stream {
        private readonly MemoryStream _inner = new(data);

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
