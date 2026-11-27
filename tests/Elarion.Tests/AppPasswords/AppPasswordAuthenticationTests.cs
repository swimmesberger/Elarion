using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using Elarion.AppPasswords;
using Elarion.AppPasswords.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elarion.Tests.AppPasswords;

/// <summary>
/// The app-password scheme over HTTP: the "Manage app passwords" and credential scenarios of spec 0006 (FR-031 to
/// FR-035) and the first adoption path (an application's own credential check).
/// </summary>
public sealed class AppPasswordAuthenticationTests {
    private static Task<AppPasswordTestHost> StartAsync(
        Action<AppPasswordAuthenticationOptions>? configureScheme = null,
        Action<IServiceCollection>? services = null) {
        return AppPasswordTestHost.StartAsync(
            s => {
                s.AddElarionAppPasswords().AddElarionInMemoryAppPasswordStore();
                services?.Invoke(s);
            },
            configureScheme);
    }

    [Fact]
    public async Task AppPassword_OverBasic_SignsTheUserInWithTheAppPasswordClaims() {
        await using var host = await StartAsync();
        var issued = await host.CreateAsync("user-1", AppPasswordScope.Dav);

        var response = await host.GetAsync("/dav/whoami", "anything@example.com", issued.Secret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AppPasswordTestHost.BodyAsync(response)).Should()
            .Be($"user-1|{issued.Password.Id}|dav|Test");
    }

    [Fact]
    public async Task NoCredential_IsAnsweredWithABasicChallenge() {
        await using var host = await StartAsync(options => options.Realm = "Example Calendars");

        var response = await host.GetAsync("/dav/whoami");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be("Basic realm=\"Example Calendars\", charset=\"UTF-8\"");
    }

    [Theory]
    [InlineData("elap_wrong")]
    [InlineData("")]
    public async Task AWrongCredential_IsAnsweredWithABasicChallenge(string password) {
        await using var host = await StartAsync();

        var response = await host.GetAsync("/dav/whoami", "user", password);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().StartWith("Basic realm=\"DAV\"");
    }

    [Fact]
    public async Task ARevokedAppPassword_IsRefusedWith401() {
        await using var host = await StartAsync();
        var issued = await host.CreateAsync("user-1", AppPasswordScope.Dav);
        (await host.GetAsync("/dav/whoami", "user", issued.Secret)).StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var scope = host.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<IAppPasswords>()
                .RevokeAsync("user-1", issued.Password.Id, TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        (await host.GetAsync("/dav/whoami", "user", issued.Secret)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ADeactivatedAccount_IsRefused() {
        await using var host = await StartAsync();
        var issued = await host.CreateAsync("deactivated", AppPasswordScope.Dav);

        var response = await host.GetAsync("/dav/whoami", "user", issued.Secret);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnAppPassword_DoesNotAuthenticateTheBrowserSurface() {
        await using var host = await StartAsync();
        var issued = await host.CreateAsync("user-1", AppPasswordScope.Dav);

        var rpc = await host.GetAsync("/rpc", "user", issued.Secret);
        var browserOnDav = await host.GetAsync("/dav/whoami", browserUser: "user-1");
        var browserOnRpc = await host.GetAsync("/rpc", browserUser: "user-1");

        rpc.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        browserOnDav.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        browserOnRpc.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AUserNameRule_RefusesTheRightPasswordWithAnotherUserName() {
        var emails = new Dictionary<string, string> { ["user-1"] = "one@example.com" };
        await using var host = await StartAsync(options => options.Events.OnValidateUserName = context =>
            ValueTask.FromResult(emails.TryGetValue(context.Match.UserId, out var email)
                                 && string.Equals(email, context.UserName, StringComparison.OrdinalIgnoreCase)));
        var issued = await host.CreateAsync("user-1", AppPasswordScope.Dav);

        var wrong = await host.GetAsync("/dav/whoami", "two@example.com", issued.Secret);
        var right = await host.GetAsync("/dav/whoami", "One@Example.com", issued.Secret);

        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        right.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ASecretContainingAColon_IsSplitAtTheFirstColon() {
        string? seenUser = null;
        string? seenSecret = null;
        await using var host = await StartAsync(options => options.Events.OnVerifyCredential = context => {
            seenUser = context.UserName;
            seenSecret = context.Secret;
            return ValueTask.FromResult<AppPasswordMatch?>(null);
        });

        await host.GetAsync("/dav/whoami", "user", "pa:ss:word");

        seenUser.Should().Be("user");
        seenSecret.Should().Be("pa:ss:word");
    }

    [Fact]
    public async Task AReplacedCredentialCheck_AuthenticatesWithoutResolvingTheStore() {
        var storeResolutions = 0;
        await using var host = await AppPasswordTestHost.StartAsync(
            services => services.AddScoped<IAppPasswordStore>(_ => {
                storeResolutions++;
                throw new InvalidOperationException("The store must not be resolved.");
            }),
            options => options.Events.OnVerifyCredential = context => ValueTask.FromResult(
                context.Secret == "legacy-token-1"
                    ? new AppPasswordMatch { Id = "legacy-7", UserId = "user-9", Scope = AppPasswordScope.Dav }
                    : null));

        var accepted = await host.GetAsync("/dav/whoami", "user", "legacy-token-1");
        var refused = await host.GetAsync("/dav/whoami", "user", "other");

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AppPasswordTestHost.BodyAsync(accepted)).Should().Be("user-9|legacy-7|dav|Test");
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        storeResolutions.Should().Be(0);
    }

    [Fact]
    public async Task AnAuthenticatedRequestThePolicyRefuses_Is403WithoutAChallenge() {
        await using var host = await StartAsync();
        var issued = await host.CreateAsync("user-1", AppPasswordScope.Dav);

        var response = await host.GetAsync("/dav/admin", "user", issued.Secret);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.WwwAuthenticate.Should().BeEmpty();
    }

    [Fact]
    public async Task AFeedToken_InTheFeedPath_SignsInWithoutAPassword() {
        await using var host = await StartAsync();
        var feed = await host.CreateAsync("user-1", AppPasswordScope.CalendarFeed("shifts", "u-1"), "Outlook");

        var response = await host.GetAsync(AppPasswordFeedPaths.GetPath("/dav", feed.Secret).Value!);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AppPasswordTestHost.BodyAsync(response)).Should()
            .Be($"user-1|{feed.Password.Id}|calendar-feed:shifts.u-1|Test");
    }

    [Fact]
    public async Task AFeedPath_RefusesAnUnknownTokenOrADavPasswordWith404AndNoChallenge() {
        await using var host = await StartAsync();
        var dav = await host.CreateAsync("user-1", AppPasswordScope.Dav);

        var unknown = await host.GetAsync("/dav/feeds/elap_unknown.ics");
        var davPassword = await host.GetAsync(AppPasswordFeedPaths.GetPath("/dav", dav.Secret).Value!);

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.Headers.WwwAuthenticate.Should().BeEmpty();
        davPassword.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AFeedToken_DoesNotWorkOverBasic() {
        await using var host = await StartAsync();
        var feed = await host.CreateAsync("user-1", AppPasswordScope.CalendarFeed("shifts", "u-1"), "Outlook");

        var response = await host.GetAsync("/dav/whoami", "user", feed.Secret);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ACustomFeedTokenReader_ReplacesTheDefaultPath() {
        await using var host = await StartAsync(options =>
            options.FeedTokenReader = AppPasswordFeedPaths.CreateReader("/calendars"));
        var feed = await host.CreateAsync("user-1", AppPasswordScope.CalendarFeed("shifts", "u-1"), "Outlook");

        var oldPath = await host.GetAsync(AppPasswordFeedPaths.GetPath("/dav", feed.Secret).Value!);

        oldPath.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "/dav/feeds is no feed path any more");
        AppPasswordFeedPaths.TryReadToken("/calendars/feeds/abc_-1.ics", "/calendars", out var token).Should().BeTrue();
        token.Should().Be("abc_-1");
        AppPasswordFeedPaths.TryReadToken("/calendars/feeds/a.b.ics", "/calendars", out _).Should().BeFalse();
    }

    [Fact]
    public async Task OnAuthenticated_ObservesEverySuccess() {
        var seen = new List<string>();
        await using var host = await StartAsync(options => options.Events.OnAuthenticated = context => {
            seen.Add($"{context.CredentialKind}:{context.Match.UserId}:{context.Principal.FindFirst(AppPasswordClaimTypes.Id)?.Value}");
            return ValueTask.CompletedTask;
        });
        var issued = await host.CreateAsync("user-1", AppPasswordScope.Dav);

        await host.GetAsync("/dav/whoami", "user", issued.Secret);
        await host.GetAsync("/dav/whoami", "user", "elap_wrong");

        seen.Should().Equal($"Basic:user-1:{issued.Password.Id}");
    }

    [Fact]
    public async Task AnEventsSubclass_RegisteredAsEventsType_IsUsed() {
        await using var host = await AppPasswordTestHost.StartAsync(
            services => services.AddElarionAppPasswords().AddElarionInMemoryAppPasswordStore()
                .AddScoped<NamedPrincipalEvents>(),
            options => options.EventsType = typeof(NamedPrincipalEvents),
            defaultPrincipals: false);
        var issued = await host.CreateAsync("user-1", AppPasswordScope.Dav);

        var response = await host.GetAsync("/dav/whoami", "user", issued.Secret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AppPasswordTestHost.BodyAsync(response)).Should().StartWith("subclass-user-1|");
    }

    [Fact]
    public async Task WithoutAPrincipalFactory_TheHostFailsToStartNamingTheIdentityDefault() {
        var start = async () => await AppPasswordTestHost.StartAsync(
            services => services.AddElarionAppPasswords().AddElarionInMemoryAppPasswordStore(),
            defaultPrincipals: false);

        (await start.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain("AddElarionIdentityAppPasswordPrincipals<TUser>()");
    }

    private sealed class NamedPrincipalEvents : AppPasswordAuthenticationEvents {
        public override ValueTask<ClaimsPrincipal?> CreatePrincipal(AppPasswordPrincipalContext context) {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "subclass-" + context.Match.UserId)], "Test");
            return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(identity));
        }
    }
}
