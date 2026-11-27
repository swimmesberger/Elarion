using System.Text;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Elarion.CalDav;
using Elarion.Calendars;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>Who may see what in the test sources; the handlers read it as the application's permissions would be read.</summary>
internal sealed class CalDavTestDirectory {
    public HashSet<string> ShiftReaders { get; } = new(StringComparer.Ordinal) { "alice", "bob" };

    public Dictionary<string, HashSet<string>> Teams { get; } = new(StringComparer.Ordinal) {
        ["red"] = new(StringComparer.Ordinal) { "alice", "bob" },
    };

    public bool GateOpen { get; set; } = true;

    public List<PublishedCalendarInfo>? Override { get; set; }
}

/// <summary>The "My shifts" source: one calendar per user, behind a permission.</summary>
public sealed record MyShiftCalendars : ICalDavSource<MyShiftCalendars> {
    public static string SourceName => "shifts";
}

/// <summary>The team source: one calendar per team the user belongs to (a resource rule).</summary>
public sealed record TeamCalendars : ICalDavSource<TeamCalendars> {
    public static string SourceName => "teams";
}

/// <summary>A source behind a feature gate: a closed gate answers not-found.</summary>
public sealed record GatedCalendars : ICalDavSource<GatedCalendars> {
    public static string SourceName => "gated";
}

internal sealed class ListMyShiftCalendars(ICurrentUser user, CalDavTestDirectory directory) : IHandler<MyShiftCalendars, Result<CalDavSourceListing>> {
    public ValueTask<Result<CalDavSourceListing>> HandleAsync(MyShiftCalendars request, CancellationToken ct) {
        if (directory.Override is { } listing) return ValueTask.FromResult(Result<CalDavSourceListing>.Success(new CalDavSourceListing(listing)));
        if (!directory.ShiftReaders.Contains(user.UserId))
            return ValueTask.FromResult(Result<CalDavSourceListing>.Failure(AppError.Forbidden("Missing permission shifts.read.")));
        return ValueTask.FromResult(Result<CalDavSourceListing>.Success(new CalDavSourceListing([
            new PublishedCalendarInfo("u-" + user.UserId, "My shifts") {
                Color = "#2E7D32", Description = "Shifts of " + user.UserId, TimeZone = CalDavTestData.Vienna,
            },
        ])));
    }
}

internal sealed class ListTeamCalendars(ICurrentUser user, CalDavTestDirectory directory) : IHandler<TeamCalendars, Result<CalDavSourceListing>> {
    public ValueTask<Result<CalDavSourceListing>> HandleAsync(TeamCalendars request, CancellationToken ct) =>
        ValueTask.FromResult(Result<CalDavSourceListing>.Success(new CalDavSourceListing([
            .. directory.Teams.Where(team => team.Value.Contains(user.UserId)).OrderBy(static t => t.Key, StringComparer.Ordinal)
                .Select(static team => new PublishedCalendarInfo(team.Key, "Team " + team.Key) { Components = CalendarComponentKinds.Event | CalendarComponentKinds.Todo }),
        ])));
}

internal sealed class ListGatedCalendars(CalDavTestDirectory directory) : IHandler<GatedCalendars, Result<CalDavSourceListing>> {
    public ValueTask<Result<CalDavSourceListing>> HandleAsync(GatedCalendars request, CancellationToken ct) =>
        ValueTask.FromResult(directory.GateOpen
            ? Result<CalDavSourceListing>.Success(new CalDavSourceListing([new PublishedCalendarInfo("all", "Gated")]))
            : Result<CalDavSourceListing>.Failure(AppError.NotFound("Not found.")));
}

internal static class CalDavTestData {
    public static TimeZoneInfo Vienna { get; } = CalendarTimeZones.TryResolve("Europe/Vienna", out var zone) ? zone : TimeZoneInfo.Utc;

    public static readonly DateTimeOffset Stamp = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    public static CalendarEvent Event(string uid, DateTime start, TimeSpan length, string summary = "Shift", TimeZoneInfo? zone = null) => new() {
        Uid = uid,
        Timestamp = Stamp,
        Summary = summary,
        Start = CalendarDateTime.Zoned(start, zone ?? Vienna),
        End = CalendarDateTime.Zoned(start + length, zone ?? Vienna),
    };
}

/// <summary>A container with CalDAV, the in-memory store (or another), the test sources and a scoped test user.</summary>
internal sealed class CalDavTestHarness : IAsyncDisposable {
    private CalDavTestHarness(ServiceProvider services) {
        Services = services;
    }

    public ServiceProvider Services { get; }

    public CalDavTestDirectory Directory => Services.GetRequiredService<CalDavTestDirectory>();

    public static CalDavTestHarness Create(Action<IServiceCollection>? configure = null, bool registerGated = true, Action<WebDavOptions>? webDav = null) {
        var services = new ServiceCollection();
        services.AddElarionCalDav();
        services.AddElarionWebDav(configure: webDav);
        services.AddElarionInMemoryDavStore();
        services.AddSingleton<CalDavTestDirectory>();
        services.AddScoped<TestUserAccessor>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<TestUserAccessor>().User);
        services.AddScoped<IHandler<MyShiftCalendars, Result<CalDavSourceListing>>, ListMyShiftCalendars>();
        services.AddScoped<IHandler<TeamCalendars, Result<CalDavSourceListing>>, ListTeamCalendars>();
        services.AddElarionCalDavSource<MyShiftCalendars>();
        services.AddElarionCalDavSource<TeamCalendars>();
        if (registerGated) {
            services.AddScoped<IHandler<GatedCalendars, Result<CalDavSourceListing>>, ListGatedCalendars>();
            services.AddElarionCalDavSource<GatedCalendars>();
        }

        configure?.Invoke(services);
        return new CalDavTestHarness(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));
    }

    public async Task<T> PublisherAsync<T>(Func<ICalDavPublisher, Task<T>> action) {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ICalDavPublisher>());
    }

    public Task<bool> PublishAsync(string source, string key, CalendarObject calendarObject) =>
        PublisherAsync(p => p.PublishAsync(new CalDavAddress(source, key), calendarObject, TestContext.Current.CancellationToken).AsTask());

    public async Task<DavTestResponse> SendAsync(string method, string path, string? body = null, string user = "alice",
        IDictionary<string, string>? headers = null) {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestUserAccessor>().User = new TestDavUser(user, user + "@example.com");
        var response = await scope.ServiceProvider.GetRequiredService<DavDispatcher>().HandleAsync(new DavRequest {
            Method = method,
            Path = path,
            HrefBase = "/dav",
            Headers = new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            Body = body is null ? Stream.Null : new MemoryStream(Encoding.UTF8.GetBytes(body)),
        }, scope.ServiceProvider, TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        await response.WriteBodyAsync(output, TestContext.Current.CancellationToken);
        return new DavTestResponse(response.StatusCode, response.Headers, response.ContentType, output.ToArray());
    }

    public const string Namespaces =
        "xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\" xmlns:CS=\"http://calendarserver.org/ns/\" xmlns:ICAL=\"http://apple.com/ns/ical/\"";

    public static string Query(string filter, string calendarData = "<C:calendar-data/>") =>
        $"<C:calendar-query {Namespaces}><D:prop><D:getetag/>{calendarData}</D:prop><C:filter>{filter}</C:filter></C:calendar-query>";

    public static string EventsIn(string start, string end) =>
        $"<C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"VEVENT\"><C:time-range start=\"{start}\" end=\"{end}\"/></C:comp-filter></C:comp-filter>";

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
