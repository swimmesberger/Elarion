using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using Elarion.CalDav;
using Elarion.Calendars;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>
/// The client against this very host (round trip): "Sync an external calendar into the application" (spec 0006, P2) and
/// FR-037–FR-043.
/// </summary>
public sealed class CalDavClientTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CalDavClient ClientFor(CalDavHost host, string user, string password) {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = host.Client.BaseAddress };
        var options = new CalDavClientOptions {
            Authorization = _ => ValueTask.FromResult<AuthenticationHeaderValue?>(
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)))),
        };
        return new CalDavClient(http, options);
    }

    private static async Task<(CalDavHost Host, CalDavClient Client)> StartAsync(Action<IServiceCollection>? services = null,
        Func<CalDavClientOptions, CalDavClientOptions>? options = null) {
        var host = await CalDavHost.StartAsync(services);
        var password = await host.CreatePasswordAsync("alice");
        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("one", new DateTime(2026, 12, 1, 6, 0, 0), TimeSpan.FromHours(8), "One"));
        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("two", new DateTime(2026, 12, 2, 6, 0, 0), TimeSpan.FromHours(8), "Two"));
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = host.Client.BaseAddress };
        var clientOptions = new CalDavClientOptions {
            Authorization = _ => ValueTask.FromResult<AuthenticationHeaderValue?>(
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice@example.com:" + password)))),
        };
        return (host, new CalDavClient(http, options?.Invoke(clientOptions) ?? clientOptions));
    }

    private static async Task<CalDavCalendar> ShiftsAsync(CalDavHost host, CalDavClient client) {
        var account = await client.DiscoverAsync(host.Client.BaseAddress!, Ct);
        account.HasValue.Should().BeTrue(account.Error?.ToString());
        return account.Value!.Calendars.Single(c => c.DisplayName == "My shifts");
    }

    [Fact]
    public async Task GivenAnAccount_WhenDiscoveredFromTheBaseUrl_ThenItsCalendarsComeWithNamesColorsAndComponentKinds() {
        var (host, client) = await StartAsync();
        await using var _ = host;

        var account = await client.DiscoverAsync(host.Client.BaseAddress!, Ct);

        account.HasValue.Should().BeTrue(account.Error?.ToString());
        account.Value!.PrincipalUrl.AbsolutePath.Should().Be("/dav/principals/alice/");
        account.Value.HomeUrl.AbsolutePath.Should().Be("/dav/calendars/alice/");
        var shifts = account.Value.Calendars.Single(c => c.Url.AbsolutePath == "/dav/calendars/alice/shifts.u-alice/");
        shifts.DisplayName.Should().Be("My shifts");
        shifts.Color.Should().Be("#2E7D32");
        shifts.Description.Should().Be("Shifts of alice");
        shifts.Components.Should().Be(CalendarComponentKinds.Event);
        shifts.IsWritable.Should().BeFalse();
        shifts.SupportsSyncCollection.Should().BeTrue();
        account.Value.Calendars.Single(c => c.DisplayName == "Team red").Components.Should().Be(CalendarComponentKinds.Event | CalendarComponentKinds.Todo);
    }

    [Fact]
    public async Task GivenACalendarAndNoState_WhenASyncStepRuns_ThenEveryObjectComesReadIntoCalendarItemsWithAState() {
        var (host, client) = await StartAsync();
        await using var _ = host;
        var shifts = await ShiftsAsync(host, client);

        var step = await client.SyncAsync(shifts, null, Ct);

        step.HasValue.Should().BeTrue(step.Error?.ToString());
        step.Value!.Changed.Select(static o => o.Name).Should().BeEquivalentTo("one.ics", "two.ics");
        step.Value.Changed.Single(o => o.Name == "one.ics").Data.Events.Single().Value!.Summary.Should().Be("One");
        step.Value.Changed.Should().AllSatisfy(o => o.ETag.Should().StartWith("\""));
        step.Value.Removed.Should().BeEmpty();
        step.Value.State.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        step.Value.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task GivenTheStoredState_WhenOneEventChangedAndOneWasDeleted_ThenTheNextStepReceivesExactlyThat() {
        var (host, client) = await StartAsync();
        await using var _ = host;
        var shifts = await ShiftsAsync(host, client);
        var first = await client.SyncAsync(shifts, null, Ct);

        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("one", new DateTime(2026, 12, 1, 7, 0, 0), TimeSpan.FromHours(8), "One moved"));
        await using (var scope = host.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICalDavPublisher>().RemoveAsync(new CalDavAddress("shifts", "u-alice"), "two", Ct);
        var next = await client.SyncAsync(shifts, first.Value!.State, Ct);
        var quiet = await client.SyncAsync(shifts, next.Value!.State, Ct);

        next.Value.Changed.Select(static o => o.Name).Should().Equal("one.ics");
        next.Value.Changed[0].Data.Events[0].Value!.Summary.Should().Be("One moved");
        next.Value.Removed.Should().Equal("two.ics");
        next.Value.State.Should().NotBe(first.Value.State);
        quiet.Value!.Changed.Should().BeEmpty();
        quiet.Value.Removed.Should().BeEmpty();
    }

    [Fact]
    public async Task GivenAnExpiredToken_WhenAStepRuns_ThenItComparesETagsAndEndsWithTheSameObjects() {
        var (host, client) = await StartAsync(services => services.Configure<WebDavOptions>(o => o.MaxJournalRowsPerCollection = 1));
        await using var _ = host;
        var shifts = await ShiftsAsync(host, client);
        var first = await client.SyncAsync(shifts, null, Ct);
        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("three", new DateTime(2026, 12, 3, 6, 0, 0), TimeSpan.FromHours(8)));
        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("one", new DateTime(2026, 12, 1, 9, 0, 0), TimeSpan.FromHours(8)));
        await using (var scope = host.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICalDavPublisher>().RemoveAsync(new CalDavAddress("shifts", "u-alice"), "two", Ct);

        var step = await client.SyncAsync(shifts, first.Value!.State, Ct);

        step.HasValue.Should().BeTrue(step.Error?.ToString());
        step.Value!.Changed.Select(static o => o.Name).Should().BeEquivalentTo("one.ics", "three.ics");
        step.Value.Removed.Should().Equal("two.ics");
        (await client.SyncAsync(shifts, step.Value.State, Ct)).Value!.Changed.Should().BeEmpty();
    }

    [Fact]
    public async Task GivenAServerThatRefusesTheCredentials_ThenTheResultSaysSoWithACode() {
        var (host, _) = await StartAsync();
        await using var __ = host;
        var client = ClientFor(host, "alice", "elap_wrong");

        var account = await client.DiscoverAsync(host.Client.BaseAddress!, Ct);

        account.HasValue.Should().BeFalse();
        account.Error!.Code.Should().Be(CalDavErrorCodes.Unauthorized);
        account.Error.StatusCode.Should().Be(401);
        account.Error.Url.Should().NotBeNull();
    }

    [Fact]
    public async Task WritingToAReadOnlyCalendar_IsAForbiddenResult_NotAnException() {
        var (host, client) = await StartAsync();
        await using var _ = host;
        var shifts = await ShiftsAsync(host, client);

        var put = await client.PutAsync(shifts, "new.ics", CalDavTestData.Event("new", new DateTime(2026, 12, 5), TimeSpan.FromHours(1)), null, Ct);
        var delete = await client.DeleteAsync(shifts, "one.ics", "\"x\"", Ct);

        put.Error!.Code.Should().Be(CalDavErrorCodes.Forbidden);
        delete.Error!.Code.Should().Be(CalDavErrorCodes.Forbidden);
    }

    [Fact]
    public async Task AStep_FetchesAtMostMaxObjectsPerStep_AndTheNextOneTheRest() {
        var (host, client) = await StartAsync(options: o => o with { MaxObjectsPerStep = 1, MultigetBatchSize = 1 });
        await using var _ = host;
        var shifts = await ShiftsAsync(host, client);

        var first = await client.SyncAsync(shifts, null, Ct);
        var second = await client.SyncAsync(shifts, first.Value!.State, Ct);
        var third = await client.SyncAsync(shifts, second.Value!.State, Ct);

        first.Value.Changed.Should().ContainSingle();
        first.Value.HasMore.Should().BeTrue();
        second.Value.Changed.Should().ContainSingle().Which.Name.Should().NotBe(first.Value.Changed[0].Name);
        second.Value.HasMore.Should().BeFalse();
        third.Value!.Changed.Should().BeEmpty();
    }

    [Fact]
    public async Task AStateFromElsewhere_IsMisconfigurationAndThrows() {
        var client = new CalDavClient(new HttpClient());

        var act = () => client.SyncAsync(new CalDavCalendar { Url = new Uri("https://example.com/cal/") }, "not-a-state!", Ct);

        await act.Should().ThrowAsync<ArgumentException>();
        FluentActions.Invoking(() => new CalDavClient(new HttpClient(), new CalDavClientOptions { MaxObjectsPerStep = 0 }))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
