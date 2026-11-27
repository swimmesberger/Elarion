using System.Net;
using AwesomeAssertions;
using Elarion.AppPasswords;
using Elarion.CalDav;
using Elarion.Calendars;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>"Subscribe to a feed from Outlook or Google Calendar" (spec 0006, P1) and FR-026, FR-027.</summary>
public sealed class CalDavFeedTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(CalDavHost Host, AppPasswordIssued Feed)> StartAsync(string owner = "alice", string calendarUser = "alice",
        Action<IServiceCollection>? services = null) {
        var host = await CalDavHost.StartAsync(services);
        await host.PublishAsync("shifts", "u-" + calendarUser,
            CalDavTestData.Event("early", new DateTime(2026, 12, 1, 6, 0, 0), TimeSpan.FromHours(8), "Early shift"));
        await host.PublishAsync("shifts", "u-" + calendarUser,
            CalDavTestData.Event("late", new DateTime(2026, 12, 1, 14, 0, 0), TimeSpan.FromHours(8), "Late shift"));
        await using var scope = host.App.Services.CreateAsyncScope();
        var issued = await scope.ServiceProvider.GetRequiredService<IAppPasswords>()
            .CreateAsync(owner, "Outlook", AppPasswordScope.CalendarFeed(new CalDavAddress("shifts", "u-" + calendarUser)), Ct);
        return (host, issued.Value);
    }

    private static string PathOf(CalDavHost host, AppPasswordIssued feed) => host.App.Services.GetRequiredService<CalDavLinks>().FeedPath(feed);

    [Fact]
    public async Task GivenAPublishedCalendar_WhenAFeedLinkIsCreated_ThenOneUrlReturnsTheCalendarAsOneIcsDocumentWithoutAPassword() {
        var (host, feed) = await StartAsync();
        await using var _ = host;

        var url = host.App.Services.GetRequiredService<CalDavLinks>().FeedUrl(new Uri("https://app.example.com/"), feed);
        var response = await host.SendAsync("GET", PathOf(host, feed));

        url.ToString().Should().Be("https://app.example.com/dav/feeds/" + feed.Secret + ".ics");
        response.Status.Should().Be(HttpStatusCode.OK);
        response.Dav.ContentType.Should().Be("text/calendar; charset=utf-8");
        var body = response.Dav.Body;
        body.Should().StartWith("BEGIN:VCALENDAR\r\n").And.EndWith("END:VCALENDAR\r\n");
        body.Split("BEGIN:VCALENDAR").Should().HaveCount(2, "the feed is one calendar");
        body.Should().Contain("X-WR-CALNAME:My shifts").And.Contain("SUMMARY:Early shift").And.Contain("SUMMARY:Late shift");
        body.Split("BEGIN:VTIMEZONE").Should().HaveCount(2, "each VTIMEZONE once (FR-027)");
    }

    [Fact]
    public async Task TheFeed_HoldsTheSameObjectsAsTheCalendar() {
        var (host, feed) = await StartAsync();
        await using var _ = host;

        var body = (await host.SendAsync("GET", PathOf(host, feed))).Dav.Body;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        var read = await new CalendarReader().ReadAsync(stream, cancellationToken: Ct);

        read.HasErrors.Should().BeFalse();
        read.Events.Select(static e => e.Uid).Should().BeEquivalentTo("early", "late");
    }

    [Fact]
    public async Task GivenAFeedLink_WhenItsAppPasswordIsRevoked_ThenTheUrlAnswers404() {
        var (host, feed) = await StartAsync();
        await using var _ = host;
        var path = PathOf(host, feed);
        (await host.SendAsync("GET", path)).Status.Should().Be(HttpStatusCode.OK);

        await using (var scope = host.App.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<IAppPasswords>().RevokeAsync("alice", feed.Password.Id, Ct)).IsSuccess.Should().BeTrue();

        var response = await host.SendAsync("GET", path);
        response.Status.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Should().NotContainKey("WWW-Authenticate");
    }

    [Fact]
    public async Task GivenAFeedLink_WhenThePersonNoLongerSeesTheCalendarThroughItsSource_ThenTheUrlAnswers404() {
        var (host, feed) = await StartAsync();
        await using var _ = host;

        host.Directory.ShiftReaders.Remove("alice");

        (await host.SendAsync("GET", PathOf(host, feed))).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AFeedTokenForACalendarItsUserDoesNotSee_Answers404() {
        var (host, feed) = await StartAsync(owner: "alice", calendarUser: "bob");
        await using var _ = host;

        (await host.SendAsync("GET", PathOf(host, feed))).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnUnknownTokenOrADavPassword_Answers404() {
        var (host, _) = await StartAsync();
        await using var __ = host;
        var dav = await host.CreatePasswordAsync("alice");

        (await host.SendAsync("GET", "/dav/feeds/elap_unknownTokenValue.ics")).Status.Should().Be(HttpStatusCode.NotFound);
        (await host.SendAsync("GET", "/dav/feeds/" + dav + ".ics")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AFeedToken_DoesNotWorkAsABasicPassword() {
        var (host, feed) = await StartAsync();
        await using var _ = host;

        (await host.SendAsync("PROPFIND", "/dav/", user: "alice", password: feed.Secret, depth: "0")).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheFeed_HonoursIfNoneMatch_AndChangesItsETagWhenTheCalendarChanges() {
        var (host, feed) = await StartAsync();
        await using var _ = host;
        var path = PathOf(host, feed);
        var first = await host.SendAsync("GET", path);
        var etag = first.Headers["ETag"];

        var notModified = await host.SendAsync("GET", path, headers: new Dictionary<string, string> { ["If-None-Match"] = etag });
        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("night", new DateTime(2026, 12, 1, 22, 0, 0), TimeSpan.FromHours(8)));
        var changed = await host.SendAsync("GET", path, headers: new Dictionary<string, string> { ["If-None-Match"] = etag });

        notModified.Status.Should().Be(HttpStatusCode.NotModified);
        changed.Status.Should().Be(HttpStatusCode.OK);
        changed.Headers["ETag"].Should().NotBe(etag);
        (await host.SendAsync("HEAD", path)).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WithFeedsDisabled_TheUrlAnswers404() {
        var (host, feed) = await StartAsync(services: s => s.Configure<CalDavOptions>(o => o.FeedsEnabled = false));
        await using var _ = host;

        (await host.SendAsync("GET", PathOf(host, feed))).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void FeedLinks_RefuseAnAppPasswordThatIsNotAFeedToken() {
        var links = new CalDavLinks(Microsoft.Extensions.Options.Options.Create(new Elarion.WebDav.WebDavOptions()));
        var dav = new AppPasswordIssued {
            Secret = "elap_x",
            Password = new AppPasswordInfo { Id = Guid.NewGuid(), UserId = "u", Name = "n", Scope = AppPasswordScope.Dav, CreatedAt = DateTimeOffset.UtcNow },
        };

        FluentActions.Invoking(() => links.FeedPath(dav)).Should().Throw<ArgumentException>();
        AppPasswordScope.CalendarFeed(new CalDavAddress("shifts", "u-1")).Value.Should().Be("calendar-feed:shifts.u-1");
    }
}
