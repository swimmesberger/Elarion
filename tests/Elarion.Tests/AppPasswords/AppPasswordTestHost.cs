using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Elarion.AppPasswords;
using Elarion.AppPasswords.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elarion.Tests.AppPasswords;

/// <summary>
/// A Kestrel host on a free loopback port with the app-password scheme beside a stand-in browser scheme (the default),
/// a DAV-like endpoint that names only the app-password scheme, a feed endpoint, and an <c>/rpc</c> endpoint on the
/// default scheme.
/// </summary>
internal sealed class AppPasswordTestHost(WebApplication app, HttpClient client) : IAsyncDisposable {
    public const string BrowserScheme = "Browser";
    public const string BrowserUserHeader = "X-Browser-User";

    public WebApplication App { get; } = app;

    public HttpClient Client { get; } = client;

    public IServiceProvider Services => App.Services;

    /// <summary>
    /// Starts the host. Without <paramref name="configureScheme"/>, principals are created by a delegate that signs in
    /// every user but <c>deactivated</c>.
    /// </summary>
    public static async Task<AppPasswordTestHost> StartAsync(
        Action<IServiceCollection>? services = null,
        Action<AppPasswordAuthenticationOptions>? configureScheme = null,
        bool defaultPrincipals = true) {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        services?.Invoke(builder.Services);
        builder.Services.AddAuthorization();
        builder.Services.AddAuthentication(BrowserScheme)
            .AddScheme<AuthenticationSchemeOptions, BrowserHandler>(BrowserScheme, null)
            .AddElarionAppPasswordAuthentication(options => {
                if (defaultPrincipals) options.Events.OnCreatePrincipal = CreatePrincipalUnlessDeactivated;
                configureScheme?.Invoke(options);
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var davOnly = new AuthorizeAttribute { AuthenticationSchemes = AppPasswordAuthenticationDefaults.AuthenticationScheme };
        app.MapGet("/dav/whoami", (HttpContext context) => Results.Text(Describe(context.User))).RequireAuthorization(davOnly);
        app.MapGet("/dav/admin", () => Results.Text("admin"))
            .RequireAuthorization(new AuthorizationPolicyBuilder(AppPasswordAuthenticationDefaults.AuthenticationScheme)
                .RequireRole("admin").Build());
        app.MapGet("/dav/feeds/{token}.ics", (HttpContext context) => Results.Text(Describe(context.User)))
            .RequireAuthorization(davOnly);
        app.MapGet("/rpc", (HttpContext context) => Results.Text(Describe(context.User))).RequireAuthorization();
        try {
            await app.StartAsync(TestContext.Current.CancellationToken);
        }
        catch {
            await app.DisposeAsync();
            throw;
        }

        var baseAddress = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new AppPasswordTestHost(app, new HttpClient { BaseAddress = new Uri(baseAddress) });
    }

    public static ValueTask<ClaimsPrincipal?> CreatePrincipalUnlessDeactivated(AppPasswordPrincipalContext context) {
        if (context.Match.UserId == "deactivated") return ValueTask.FromResult<ClaimsPrincipal?>(null);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, context.Match.UserId)], "Test");
        return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(identity));
    }

    /// <summary>Creates an app password through the host's <see cref="IAppPasswords"/>.</summary>
    public async Task<AppPasswordIssued> CreateAsync(string userId, AppPasswordScope scope, string name = "iPhone") {
        await using var serviceScope = Services.CreateAsyncScope();
        var issued = await serviceScope.ServiceProvider.GetRequiredService<IAppPasswords>()
            .CreateAsync(userId, name, scope, TestContext.Current.CancellationToken);
        return issued.Value;
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? user = null, string? password = null,
        string? browserUser = null) {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (password is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes((user ?? "") + ":" + password)));
        if (browserUser is not null) request.Headers.Add(BrowserUserHeader, browserUser);
        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<string> BodyAsync(HttpResponseMessage response) {
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        await App.StopAsync(TestContext.Current.CancellationToken);
        await App.DisposeAsync();
    }

    private static string Describe(ClaimsPrincipal user) {
        return string.Join("|",
            user.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            user.FindFirst(AppPasswordClaimTypes.Id)?.Value,
            user.FindFirst(AppPasswordClaimTypes.Scope)?.Value,
            user.Identity?.AuthenticationType);
    }

    /// <summary>Stands in for the application's browser credential (a cookie or a proxy token).</summary>
    private sealed class BrowserHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder) {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
            if (!Request.Headers.TryGetValue(BrowserUserHeader, out var user))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], BrowserScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), BrowserScheme)));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties) {
            Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return Task.CompletedTask;
        }
    }
}
