using AwesomeAssertions;
using Elarion.CalDav;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>A calendar's explicit URL segment (spec 0006 FR-014): URLs devices already stored keep working.</summary>
public sealed class CalDavSegmentTests {
    [Fact]
    public async Task ACalendarWithASegment_IsServedThere_AndTheDefaultSegmentIsNot() {
        await using var harness = CalDavTestHarness.Create();
        harness.Directory.Override = [new PublishedCalendarInfo("all", "Shifts") { Segment = "work" }];
        await harness.PublishAsync("shifts", "all", CalDavTestData.Event("e-1", new DateTime(2026, 12, 1, 8, 0, 0), TimeSpan.FromHours(8)));

        var home = await harness.SendAsync("PROPFIND", "/calendars/alice/", DavTestHarness.PropFind("D:displayname"),
            headers: new Dictionary<string, string> { ["Depth"] = "1" });
        var get = await harness.SendAsync("GET", "/calendars/alice/work/e-1.ics");
        var old = await harness.SendAsync("GET", "/calendars/alice/shifts.all/e-1.ics");

        home.Prop("/dav/calendars/alice/work/", DavNames.D.DisplayName)!.Text.Should().Be("Shifts");
        get.Status.Should().Be(200);
        old.Status.Should().Be(404);
    }
}
