using AwesomeAssertions;
using Elarion.CalDav;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Elarion.WebDav.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>Publishing in the handler's transaction and serving from several nodes, on the EF store over real PostgreSQL.</summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlCalDavTests(PostgreSqlDavStoreFixture fixture) : IClassFixture<PostgreSqlDavStoreFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CalDavTestHarness Node() => CalDavTestHarness.Create(services => {
        services.AddDbContext<DavStoreIntegrationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddElarionWebDavEntityFrameworkCore<DavStoreIntegrationDbContext>();
    });

    [Fact]
    public async Task GivenAShiftPublishedInItsHandler_WhenTheTransactionCommits_ThenTheEventIsInThePublishedCalendar_AndWhenItRollsBackItIsNot() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var harness = Node();
        var user = "pg" + Guid.NewGuid().ToString("N")[..8];
        harness.Directory.ShiftReaders.Add(user);
        var address = new CalDavAddress("shifts", "u-" + user);
        var path = $"/calendars/{user}/shifts.u-{user}/";

        await using (var scope = harness.Services.CreateAsyncScope()) {
            var context = scope.ServiceProvider.GetRequiredService<DavStoreIntegrationDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<ICalDavPublisher>()
                .PublishAsync(address, CalDavTestData.Event("rolled-back", new DateTime(2026, 12, 1, 6, 0, 0), TimeSpan.FromHours(8)), Ct);
            await transaction.RollbackAsync(Ct);
        }

        await using (var scope = harness.Services.CreateAsyncScope()) {
            var context = scope.ServiceProvider.GetRequiredService<DavStoreIntegrationDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<ICalDavPublisher>()
                .PublishAsync(address, CalDavTestData.Event("committed", new DateTime(2026, 12, 2, 6, 0, 0), TimeSpan.FromHours(8)), Ct);
            (await harness.SendAsync("GET", path + "committed.ics", user: user)).Status.Should().Be(404, "it is not committed yet");
            await transaction.CommitAsync(Ct);
        }

        var sync = await harness.SendAsync("REPORT", path, DavTestHarness.SyncCollection(null), user: user);
        sync.Responses.Keys.Should().Equal($"/dav/calendars/{user}/shifts.u-{user}/committed.ics");
        sync.SyncToken.Should().EndWith(":1");
    }

    [Fact]
    public async Task GivenTwoNodes_WhenAClientSyncsAlternatelyAgainstBoth_ThenEachAnswerContinuesThePreviousOne() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var nodeA = Node();
        await using var nodeB = Node();
        var user = "two" + Guid.NewGuid().ToString("N")[..8];
        nodeA.Directory.ShiftReaders.Add(user);
        nodeB.Directory.ShiftReaders.Add(user);
        var path = $"/calendars/{user}/shifts.u-{user}/";
        var href = $"/dav/calendars/{user}/shifts.u-{user}/";

        await nodeA.PublishAsync("shifts", "u-" + user, CalDavTestData.Event("one", new DateTime(2026, 12, 1, 6, 0, 0), TimeSpan.FromHours(8)));
        var first = await nodeB.SendAsync("REPORT", path, DavTestHarness.SyncCollection(null), user: user);
        await nodeB.PublishAsync("shifts", "u-" + user, CalDavTestData.Event("two", new DateTime(2026, 12, 2, 6, 0, 0), TimeSpan.FromHours(8)));
        var second = await nodeA.SendAsync("REPORT", path, DavTestHarness.SyncCollection(first.SyncToken), user: user);
        await nodeA.PublisherAsync(p => p.RemoveAsync(new CalDavAddress("shifts", "u-" + user), "one", Ct).AsTask());
        var third = await nodeB.SendAsync("REPORT", path, DavTestHarness.SyncCollection(second.SyncToken), user: user);

        first.Responses.Keys.Should().Equal(href + "one.ics");
        second.Responses.Keys.Should().Equal(href + "two.ics");
        third.Responses.Keys.Should().Equal(href + "one.ics");
        third.Responses[href + "one.ics"].Child(DavNames.D.Status)!.Text.Should().Contain("404");
        (await nodeA.SendAsync("REPORT", path, DavTestHarness.SyncCollection(third.SyncToken), user: user)).Responses.Should().BeEmpty();
    }

    [Fact]
    public async Task CalendarQuery_SelectsCandidatesByTheStoredSpanAndAnswersTheExactMatches() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var harness = Node();
        var user = "q" + Guid.NewGuid().ToString("N")[..8];
        harness.Directory.ShiftReaders.Add(user);
        var weekly = CalDavTestData.Event("weekly", new DateTime(2025, 11, 3, 9, 0, 0), TimeSpan.FromHours(1));
        weekly.RecurrenceRules.Add(new Elarion.Calendars.CalendarRecurrenceRule { Frequency = Elarion.Calendars.CalendarFrequency.Weekly });
        await harness.PublishAsync("shifts", "u-" + user, weekly);
        await harness.PublishAsync("shifts", "u-" + user, CalDavTestData.Event("old", new DateTime(2026, 10, 15, 8, 0, 0), TimeSpan.FromHours(2)));

        var response = await harness.SendAsync("REPORT", $"/calendars/{user}/shifts.u-{user}/",
            CalDavTestHarness.Query(CalDavTestHarness.EventsIn("20261116T000000Z", "20261123T000000Z")), user: user,
            headers: new Dictionary<string, string> { ["Depth"] = "1" });

        response.Responses.Keys.Should().Equal($"/dav/calendars/{user}/shifts.u-{user}/weekly.ics");
    }
}
