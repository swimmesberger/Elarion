using AwesomeAssertions;
using Elarion.CalDav;
using Elarion.Calendars;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>"Publish a calendar a person sees on their phone" (spec 0006, P1) and FR-004–FR-009.</summary>
public sealed class CalDavPublishingTests {
    private const string Calendar = "/calendars/alice/shifts.u-alice/";
    private const string CalendarHref = "/dav/calendars/alice/shifts.u-alice/";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> TokenAsync(CalDavTestHarness harness) =>
        (await harness.SendAsync("REPORT", Calendar, DavTestHarness.SyncCollection(null))).SyncToken!;

    [Fact]
    public async Task GivenAPublishedEvent_WhenItIsPublishedAgainUnchanged_ThenTheSyncTokenDoesNotChange() {
        await using var harness = CalDavTestHarness.Create();
        var shift = CalDavTestData.Event("shift-1", new DateTime(2026, 12, 1, 6, 0, 0), TimeSpan.FromHours(8));
        (await harness.PublishAsync("shifts", "u-alice", shift)).Should().BeTrue();
        var token = await TokenAsync(harness);

        (await harness.PublishAsync("shifts", "u-alice", CalDavTestData.Event("shift-1", new DateTime(2026, 12, 1, 6, 0, 0), TimeSpan.FromHours(8))))
            .Should().BeFalse();

        (await TokenAsync(harness)).Should().Be(token);
    }

    [Fact]
    public async Task GivenAPublishedEvent_WhenItsStartChanges_ThenTokenAndETagChangeAndASyncFromTheOldTokenReceivesIt() {
        await using var harness = CalDavTestHarness.Create();
        await harness.PublishAsync("shifts", "u-alice", CalDavTestData.Event("shift-1", new DateTime(2026, 12, 1, 6, 0, 0), TimeSpan.FromHours(8)));
        var initial = await harness.SendAsync("REPORT", Calendar, DavTestHarness.SyncCollection(null));
        var etag = initial.Prop(CalendarHref + "shift-1.ics", DavNames.D.GetETag)!.Text;

        await harness.PublishAsync("shifts", "u-alice", CalDavTestData.Event("shift-1", new DateTime(2026, 12, 1, 7, 0, 0), TimeSpan.FromHours(8)));
        var delta = await harness.SendAsync("REPORT", Calendar, DavTestHarness.SyncCollection(initial.SyncToken, props: "C:calendar-data")
            .Replace("xmlns:T=\"urn:elarion:test\"", "xmlns:C=\"urn:ietf:params:xml:ns:caldav\""));

        delta.SyncToken.Should().NotBe(initial.SyncToken);
        delta.Responses.Keys.Should().Equal(CalendarHref + "shift-1.ics");
        delta.Prop(CalendarHref + "shift-1.ics", DavNames.D.GetETag)!.Text.Should().NotBe(etag);
        delta.Prop(CalendarHref + "shift-1.ics", DavNames.C.CalendarData)!.Text.Should().Contain("DTSTART;TZID=Europe/Vienna:20261201T070000");
    }

    [Fact]
    public async Task GivenAPublishedEvent_WhenItIsRemoved_ThenASyncFromTheOldTokenReceivesItAsRemoved() {
        await using var harness = CalDavTestHarness.Create();
        await harness.PublishAsync("shifts", "u-alice", CalDavTestData.Event("shift-1", new DateTime(2026, 12, 1, 6, 0, 0), TimeSpan.FromHours(8)));
        var token = await TokenAsync(harness);

        (await harness.PublisherAsync(p => p.RemoveAsync(new CalDavAddress("shifts", "u-alice"), "shift-1", Ct).AsTask())).Should().BeTrue();
        var delta = await harness.SendAsync("REPORT", Calendar, DavTestHarness.SyncCollection(token));

        delta.Responses[CalendarHref + "shift-1.ics"].Child(DavNames.D.Status)!.Text.Should().Contain("404");
    }

    [Fact]
    public async Task GivenA300EventCalendar_WhenReplacedWith299OneChanged_ThenASyncReceivesExactlyOneChangeAndOneRemoval() {
        await using var harness = CalDavTestHarness.Create();
        var address = new CalDavAddress("shifts", "u-alice");
        var start = new DateTime(2026, 12, 1, 6, 0, 0);
        await harness.PublisherAsync(p => p.ReplaceAsync(address,
            Enumerable.Range(0, 300).Select(i => (CalendarObject)CalDavTestData.Event("s" + i, start.AddDays(i), TimeSpan.FromHours(8))), Ct).AsTask());
        var token = await TokenAsync(harness);

        var outcome = await harness.PublisherAsync(p => p.ReplaceAsync(address,
            Enumerable.Range(0, 299).Select(i => (CalendarObject)CalDavTestData.Event("s" + i, start.AddDays(i), TimeSpan.FromHours(i == 42 ? 9 : 8))), Ct)
            .AsTask());
        var delta = await harness.SendAsync("REPORT", Calendar, DavTestHarness.SyncCollection(token));

        outcome.Should().Be(new DavReplaceOutcome(0, 1, 1));
        delta.Responses.Keys.Should().BeEquivalentTo(CalendarHref + "s42.ics", CalendarHref + "s299.ics");
        delta.Responses[CalendarHref + "s299.ics"].Child(DavNames.D.Status)!.Text.Should().Contain("404");
    }

    [Fact]
    public async Task GivenAWeeklySeriesWithOneMovedOccurrence_WhenPublished_ThenItIsOneObjectUnderOneUidWithAVTimezone() {
        await using var harness = CalDavTestHarness.Create();
        var master = CalDavTestData.Event("weekly-1", new DateTime(2026, 12, 7, 9, 0, 0), TimeSpan.FromHours(1), "Stand-up");
        master.RecurrenceRules.Add(new CalendarRecurrenceRule { Frequency = CalendarFrequency.Weekly });
        var moved = CalDavTestData.Event("weekly-1", new DateTime(2026, 12, 15, 10, 0, 0), TimeSpan.FromHours(1), "Stand-up (moved)");
        moved.RecurrenceId = CalendarDateTime.Zoned(new DateTime(2026, 12, 14, 9, 0, 0), CalDavTestData.Vienna);

        await harness.PublishAsync("shifts", "u-alice", CalendarObject.FromItems(master, moved));
        var get = await harness.SendAsync("GET", Calendar + "weekly-1.ics");

        get.Status.Should().Be(200);
        get.ContentType.Should().Be("text/calendar; charset=utf-8; component=vevent");
        var body = get.Body;
        body.Split("BEGIN:VEVENT").Should().HaveCount(3);
        body.Split("BEGIN:VTIMEZONE").Should().HaveCount(2);
        body.Should().Contain("TZID:Europe/Vienna").And.Contain("RRULE:FREQ=WEEKLY").And.Contain("RECURRENCE-ID;TZID=Europe/Vienna:20261214T090000");
        body.Should().NotContain("METHOD");
    }

    [Fact]
    public async Task PublishingAnObjectWithAMethod_Throws() {
        await using var harness = CalDavTestHarness.Create();
        var component = CalDavTestData.Event("m-1", new DateTime(2026, 12, 1, 6, 0, 0), TimeSpan.FromHours(1)).ToComponent();
        component.Properties.Add(new CalendarProperty("METHOD", "REQUEST"));

        var act = () => harness.PublishAsync("shifts", "u-alice", component);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*METHOD*");
    }

    [Fact]
    public async Task PublishingAnItemWithoutDtStampOrLastModified_Throws() {
        await using var harness = CalDavTestHarness.Create();
        var item = CalDavTestData.Event("n-1", new DateTime(2026, 12, 1, 6, 0, 0), TimeSpan.FromHours(1));
        item.Timestamp = null;

        await FluentActions.Awaiting(() => harness.PublishAsync("shifts", "u-alice", item)).Should().ThrowAsync<CalendarWriteException>();
    }

    [Fact]
    public async Task PublishingIntoASourceThatIsNotRegistered_Throws() {
        await using var harness = CalDavTestHarness.Create();

        await FluentActions.Awaiting(() => harness.PublishAsync("nope", "k", CalDavTestData.Event("x", new DateTime(2026, 12, 1), TimeSpan.FromHours(1))))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*'nope'*");
    }

    [Fact]
    public void ComponentsOfDifferentUids_AreRefused() {
        var a = CalDavTestData.Event("a", new DateTime(2026, 12, 1), TimeSpan.FromHours(1));
        var b = CalDavTestData.Event("b", new DateTime(2026, 12, 1), TimeSpan.FromHours(1));

        FluentActions.Invoking(() => CalendarObject.FromItems(a, b)).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("shift-1@example.com", "shift-1@example.com.ics")]
    [InlineData("a/b", "")]
    [InlineData("ünïcode", "")]
    public void ObjectNames_AreTheUidOrItsHash_AndStable(string uid, string expected) {
        var name = CalDavObjects.NameOf(uid);

        name.Should().Be(CalDavObjects.NameOf(uid));
        if (expected.Length > 0) name.Should().Be(expected);
        else name.Should().MatchRegex("^[a-z2-7]{52}\\.ics$");
    }

    [Fact]
    public async Task TheStoredSpan_CoversTheFirstAndLastOccurrence_AndIsOpenForAnEndlessSeries() {
        await using var harness = CalDavTestHarness.Create();
        var endless = CalDavTestData.Event("endless", new DateTime(2025, 1, 6, 9, 0, 0), TimeSpan.FromHours(1));
        endless.RecurrenceRules.Add(new CalendarRecurrenceRule { Frequency = CalendarFrequency.Daily });
        var counted = CalDavTestData.Event("counted", new DateTime(2026, 1, 5, 9, 0, 0), TimeSpan.FromHours(1));
        counted.RecurrenceRules.Add(new CalendarRecurrenceRule { Frequency = CalendarFrequency.Weekly, Count = 3 });
        await harness.PublishAsync("shifts", "u-alice", endless);
        await harness.PublishAsync("shifts", "u-alice", counted);

        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDavCollectionStore>();
        var id = new CalDavAddress("shifts", "u-alice").ToCollectionAddress().Id;
        var far = await store.GetCandidatesAsync(id, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2030, 1, 2, 0, 0, 0, TimeSpan.Zero), Ct).Select(static o => o.Name).ToListAsync(Ct);
        var third = await store.GetCandidatesAsync(id, new DateTimeOffset(2026, 1, 19, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero), Ct).Select(static o => o.Name).ToListAsync(Ct);
        var after = await store.GetCandidatesAsync(id, new DateTimeOffset(2026, 1, 27, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 28, 0, 0, 0, TimeSpan.Zero), Ct).Select(static o => o.Name).ToListAsync(Ct);

        far.Should().Equal("endless.ics");
        third.Should().Equal("counted.ics", "endless.ics");
        after.Should().Equal("endless.ics");
    }
}
