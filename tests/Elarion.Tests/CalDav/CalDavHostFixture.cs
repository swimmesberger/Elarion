using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Elarion.Abstractions;
using Elarion.AppPasswords;
using Elarion.AppPasswords.AspNetCore;
using Elarion.AspNetCore;
using Elarion.CalDav;
using Elarion.Identity;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Elarion.WebDav.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>
/// A Kestrel host serving CalDAV under <c>/dav</c> with app passwords beside a stand-in browser scheme, cross-origin
/// protection before routing, and an <c>/rpc</c> endpoint on the browser scheme: the shape of an application's host.
/// </summary>
internal sealed class CalDavHost(WebApplication app, HttpClient client) : IAsyncDisposable {
    public const string BrowserScheme = "Browser";
    public const string BrowserUserHeader = "X-Browser-User";

    public WebApplication App { get; } = app;

    public HttpClient Client { get; } = client;

    public CalDavTestDirectory Directory => App.Services.GetRequiredService<CalDavTestDirectory>();

    public static async Task<CalDavHost> StartAsync(Action<IServiceCollection>? services = null, Action<WebApplication>? beforeRouting = null) {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddElarionClaimsCurrentUser();
        builder.Services.AddElarionCalDav();
        builder.Services.AddElarionInMemoryDavStore();
        builder.Services.AddElarionAppPasswords().AddElarionInMemoryAppPasswordStore();
        builder.Services.AddSingleton<CalDavTestDirectory>();
        builder.Services.AddScoped<IHandler<MyShiftCalendars, Result<CalDavSourceListing>>, ListMyShiftCalendars>();
        builder.Services.AddScoped<IHandler<TeamCalendars, Result<CalDavSourceListing>>, ListTeamCalendars>();
        builder.Services.AddElarionCalDavSource<MyShiftCalendars>();
        builder.Services.AddElarionCalDavSource<TeamCalendars>();
        services?.Invoke(builder.Services);
        builder.Services.AddAuthorization();
        builder.Services.AddAuthentication(BrowserScheme)
            .AddScheme<AuthenticationSchemeOptions, BrowserHandler>(BrowserScheme, null)
            .AddElarionAppPasswordAuthentication(options => options.Events.OnCreatePrincipal = context => {
                if (context.Match.UserId == "deactivated") return ValueTask.FromResult<ClaimsPrincipal?>(null);
                var identity = new ClaimsIdentity([new Claim("sub", context.Match.UserId), new Claim("email", context.Match.UserId + "@example.com")],
                    "AppPassword");
                return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(identity));
            });

        var app = builder.Build();
        app.UseElarionCrossOriginProtection();
        beforeRouting?.Invoke(app);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapElarionDav();
        app.MapPost("/rpc", (HttpContext context) => Results.Text("rpc:" + context.User.FindFirst("sub")?.Value)).RequireAuthorization();
        try {
            await app.StartAsync(TestContext.Current.CancellationToken);
        }
        catch {
            await app.DisposeAsync();
            throw;
        }

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new CalDavHost(app, new HttpClient(handler) { BaseAddress = new Uri(address) });
    }

    public async Task<string> CreatePasswordAsync(string userId, AppPasswordScope? scope = null) {
        await using var serviceScope = App.Services.CreateAsyncScope();
        var issued = await serviceScope.ServiceProvider.GetRequiredService<IAppPasswords>()
            .CreateAsync(userId, "iPhone", scope ?? AppPasswordScope.Dav, TestContext.Current.CancellationToken);
        return issued.Value.Secret;
    }

    public async Task PublishAsync(string source, string key, Elarion.Calendars.CalendarObject calendarObject) {
        await using var scope = App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICalDavPublisher>()
            .PublishAsync(new CalDavAddress(source, key), calendarObject, TestContext.Current.CancellationToken);
    }

    /// <summary>Sends a request as a calendar app does; <paramref name="password"/> null sends none.</summary>
    public async Task<HostResponse> SendAsync(string method, string path, string? body = null, string? user = null, string? password = null,
        string? depth = null, IDictionary<string, string>? headers = null) {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        if (password is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes((user ?? "") + ":" + password)));
        if (depth is not null) request.Headers.Add("Depth", depth);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var responseHeaders = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(static h => h.Key, static h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        return new HostResponse(response.StatusCode, responseHeaders, new DavTestResponse((int)response.StatusCode, responseHeaders,
            response.Content.Headers.ContentType?.ToString(), bytes));
    }

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        await App.StopAsync(TestContext.Current.CancellationToken);
        await App.DisposeAsync();
    }

    /// <summary>Stands in for the application's browser credential (a cookie or a proxy token).</summary>
    private sealed class BrowserHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder) {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
            if (!Request.Headers.TryGetValue(BrowserUserHeader, out var user)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", user.ToString())], BrowserScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), BrowserScheme)));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties) {
            Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return Task.CompletedTask;
        }
    }
}

/// <summary>What the host answered.</summary>
internal sealed record HostResponse(HttpStatusCode Status, IReadOnlyDictionary<string, string> Headers, DavTestResponse Dav);
