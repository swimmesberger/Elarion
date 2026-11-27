using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.CalDav;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>"Decide who sees which calendar in a handler" (spec 0006, P1) and FR-010–FR-013.</summary>
public sealed class CalDavSourceTests {
    private static readonly string HomeQuery = DavTestHarness.PropFind("D:displayname", "D:resourcetype", "ICAL:calendar-color",
        "C:calendar-description", "C:supported-calendar-component-set", "C:calendar-timezone", "D:current-user-privilege-set");

    private static Dictionary<string, string> Depth(string depth) => new() { ["Depth"] = depth };

    private static IEnumerable<string> Calendars(DavTestResponse home, string user) =>
        home.Responses.Keys.Where(href => href != $"/dav/calendars/{user}/");

    [Fact]
    public async Task GivenAUserWithoutThePermission_ThenTheHomeListsNoShiftCalendarAndItsUrlAnswers404() {
        await using var harness = CalDavTestHarness.Create();
        harness.Directory.ShiftReaders.Remove("carol");

        var home = await harness.SendAsync("PROPFIND", "/calendars/carol/", HomeQuery, user: "carol", headers: Depth("1"));
        var direct = await harness.SendAsync("PROPFIND", "/calendars/carol/shifts.u-carol/", HomeQuery, user: "carol", headers: Depth("0"));

        Calendars(home, "carol").Should().NotContain(href => href.Contains("shifts.", StringComparison.Ordinal));
        direct.Status.Should().Be(404);
    }

    [Fact]
    public async Task GivenAUserWithThePermission_ThenTheHomeListsExactlyTheirOwnShiftCalendarWithItsNameColorAndDescription() {
        await using var harness = CalDavTestHarness.Create();
        harness.Directory.Teams.Clear();
        harness.Directory.GateOpen = false;

        var home = await harness.SendAsync("PROPFIND", "/calendars/alice/", HomeQuery, user: "alice", headers: Depth("1"));

        Calendars(home, "alice").Should().Equal("/dav/calendars/alice/shifts.u-alice/");
        const string href = "/dav/calendars/alice/shifts.u-alice/";
        home.Prop(href, DavNames.D.DisplayName)!.Text.Should().Be("My shifts");
        home.Prop(href, new DavName(DavNames.AppleIcal, "calendar-color"))!.Text.Should().Be("#2E7D32");
        home.Prop(href, DavNames.C.CalendarDescription)!.Text.Should().Be("Shifts of alice");
        home.Prop(href, DavNames.D.ResourceType)!.Child(DavNames.C.Calendar).Should().NotBeNull();
        home.Prop(href, DavNames.C.SupportedCalendarComponentSet)!.Children.Select(static c => c.Attribute("name")).Should().Equal("VEVENT");
        home.Prop(href, DavNames.C.CalendarTimezone)!.Text.Should().Contain("BEGIN:VTIMEZONE").And.Contain("TZID:Europe/Vienna");
        (await harness.SendAsync("PROPFIND", "/calendars/alice/shifts.u-bob/", HomeQuery, user: "alice", headers: Depth("0"))).Status.Should().Be(404);
    }

    [Fact]
    public async Task GivenATeamCalendar_WhenAMemberLeavesTheTeam_ThenOnTheNextSyncTheCalendarIsGoneFromTheirHome() {
        await using var harness = CalDavTestHarness.Create();

        var before = await harness.SendAsync("PROPFIND", "/calendars/bob/", HomeQuery, user: "bob", headers: Depth("1"));
        harness.Directory.Teams["red"].Remove("bob");
        var after = await harness.SendAsync("PROPFIND", "/calendars/bob/", HomeQuery, user: "bob", headers: Depth("1"));

        Calendars(before, "bob").Should().Contain("/dav/calendars/bob/teams.red/");
        Calendars(after, "bob").Should().NotContain("/dav/calendars/bob/teams.red/");
        (await harness.SendAsync("REPORT", "/calendars/bob/teams.red/", DavTestHarness.SyncCollection(null), user: "bob")).Status.Should().Be(404);
    }

    [Fact]
    public async Task GivenTheSourcesModuleDisabled_ThenNoCalendarOfTheSourceIsListedOrServed() {
        await using var harness = CalDavTestHarness.Create(registerGated: false);

        var home = await harness.SendAsync("PROPFIND", "/calendars/alice/", HomeQuery, headers: Depth("1"));

        Calendars(home, "alice").Should().NotContain(href => href.Contains("gated.", StringComparison.Ordinal));
        (await harness.SendAsync("PROPFIND", "/calendars/alice/gated.all/", HomeQuery, headers: Depth("0"))).Status.Should().Be(404);
    }

    [Fact]
    public async Task GivenTheSourcesFeatureGateClosed_ThenNoCalendarOfTheSourceIsListedOrServed() {
        await using var harness = CalDavTestHarness.Create();
        (await harness.SendAsync("PROPFIND", "/calendars/alice/gated.all/", HomeQuery, headers: Depth("0"))).Status.Should().Be(207);

        harness.Directory.GateOpen = false;

        var home = await harness.SendAsync("PROPFIND", "/calendars/alice/", HomeQuery, headers: Depth("1"));
        Calendars(home, "alice").Should().NotContain(href => href.Contains("gated.", StringComparison.Ordinal));
        (await harness.SendAsync("PROPFIND", "/calendars/alice/gated.all/", HomeQuery, headers: Depth("0"))).Status.Should().Be(404);
    }

    [Fact]
    public async Task GivenAPrincipalOrHomeUrlWithAnotherUsersId_ThenTheAnswerIs404() {
        await using var harness = CalDavTestHarness.Create();

        (await harness.SendAsync("PROPFIND", "/principals/bob/", HomeQuery, user: "alice", headers: Depth("0"))).Status.Should().Be(404);
        (await harness.SendAsync("PROPFIND", "/calendars/bob/", HomeQuery, user: "alice", headers: Depth("1"))).Status.Should().Be(404);
        (await harness.SendAsync("GET", "/calendars/bob/shifts.u-bob/x.ics", user: "alice")).Status.Should().Be(404);
    }

    [Fact]
    public async Task ThePrincipal_AnswersTheCalendarHomeAndTheUsersAddress() {
        await using var harness = CalDavTestHarness.Create();

        var principal = await harness.SendAsync("PROPFIND", "/principals/alice/",
            DavTestHarness.PropFind("C:calendar-home-set", "C:calendar-user-address-set", "D:current-user-principal"), headers: Depth("0"));

        principal.Prop("/dav/principals/alice/", DavNames.C.CalendarHomeSet)!.Child(DavNames.D.Href)!.Text.Should().Be("/dav/calendars/alice/");
        principal.Prop("/dav/principals/alice/", DavNames.C.CalendarUserAddressSet)!.ChildrenNamed(DavNames.D.Href).Select(static h => h.Text)
            .Should().Contain("mailto:alice@example.com");
        (await harness.SendAsync("OPTIONS", "/principals/alice/")).Headers["DAV"].Should().Be("1, 3, calendar-access");
    }

    [Fact]
    public async Task ASourceReturningAnInvalidOrDuplicateKey_FailsNamingTheSource() {
        await using var harness = CalDavTestHarness.Create();
        harness.Directory.Override = [new PublishedCalendarInfo("bad key", "Bad")];

        await FluentActions.Awaiting(() => harness.SendAsync("PROPFIND", "/calendars/alice/", HomeQuery, headers: Depth("1")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*'shifts'*");
        harness.Directory.Override = [new PublishedCalendarInfo("k", "A"), new PublishedCalendarInfo("k", "B")];
        await FluentActions.Awaiting(() => harness.SendAsync("PROPFIND", "/calendars/alice/", HomeQuery, headers: Depth("1")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*twice*");
    }

    [Fact]
    public void TwoSourcesWithOneName_FailAtStartup() {
        var services = new ServiceCollection();
        services.AddElarionCalDavSource<MyShiftCalendars>();

        FluentActions.Invoking(() => services.AddElarionCalDavSource<ShiftsAgain>()).Should().Throw<InvalidOperationException>().WithMessage("*'shifts'*");
        FluentActions.Invoking(() => services.AddElarionCalDavSource<BadName>()).Should().Throw<InvalidOperationException>().WithMessage("*BadName*");
    }

    [Fact]
    public void AddressesWithInvalidSegments_AreRefused() {
        FluentActions.Invoking(() => new CalDavAddress("a.b", "k")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new CalDavAddress("s", new string('k', 65))).Should().Throw<ArgumentException>();
        new CalDavAddress("shifts", "u-1_A").ToString().Should().Be("shifts.u-1_A");
    }

    public sealed record ShiftsAgain : ICalDavSource<ShiftsAgain> {
        public static string SourceName => "shifts";
    }

    public sealed record BadName : ICalDavSource<BadName> {
        public static string SourceName => "has space";
    }
}
