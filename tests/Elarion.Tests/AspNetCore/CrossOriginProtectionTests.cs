using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// <c>UseElarionCrossOriginProtection</c>: state-changing requests and WebSocket handshakes a browser sent from another
/// origin are refused (Fetch Metadata first, <c>Origin</c> as the fallback), other safe-method requests, exempt
/// prefixes, and callers without either header pass.
/// </summary>
public sealed class CrossOriginProtectionTests {
    private const string OwnOrigin = "https://api.example.com";
    private const string AllowedOrigin = "https://app.example.com";
    private const string Attacker = "https://attacker.example";
    private const string Sibling = "https://evil.example.com";

    [Theory]
    // Safe methods pass whatever their origin.
    [InlineData("GET", Attacker, "cross-site", "/rpc", true)]
    [InlineData("HEAD", Attacker, "cross-site", "/rpc", true)]
    [InlineData("OPTIONS", Attacker, "cross-site", "/rpc", true)]
    [InlineData("TRACE", Attacker, "cross-site", "/rpc", true)]
    // Neither header: not a browser page (server-to-server, SSR, scripts).
    [InlineData("POST", null, null, "/rpc", true)]
    // Fetch Metadata present.
    [InlineData("POST", OwnOrigin, "same-origin", "/rpc", true)]
    [InlineData("POST", null, "same-origin", "/rpc", true)]
    [InlineData("POST", null, "none", "/rpc", true)]
    [InlineData("POST", Attacker, "cross-site", "/rpc", false)]
    [InlineData("POST", null, "cross-site", "/rpc", false)]
    [InlineData("POST", Sibling, "same-site", "/rpc", false)]
    [InlineData("POST", AllowedOrigin, "same-site", "/rpc", true)]
    [InlineData("POST", AllowedOrigin, "cross-site", "/rpc", true)]
    [InlineData("POST", "https://APP.example.com:443", "cross-site", "/rpc", true)]
    [InlineData("POST", Attacker, "unknown-value", "/rpc", false)]
    [InlineData("PUT", Attacker, "cross-site", "/items/1", false)]
    [InlineData("PATCH", Attacker, "cross-site", "/items/1", false)]
    [InlineData("DELETE", Attacker, "cross-site", "/items/1", false)]
    // No Fetch Metadata (older browsers): Origin decides.
    [InlineData("POST", OwnOrigin, null, "/rpc", true)]
    [InlineData("POST", AllowedOrigin, null, "/rpc", true)]
    [InlineData("POST", Attacker, null, "/rpc", false)]
    [InlineData("POST", "http://api.example.com", null, "/rpc", false)]
    [InlineData("POST", "null", null, "/rpc", false)]
    [InlineData("POST", "not an origin", null, "/rpc", false)]
    // Exempt prefixes match whole segments, case-insensitively.
    [InlineData("POST", Attacker, "cross-site", "/signin-oidc", true)]
    [InlineData("POST", Attacker, "cross-site", "/SIGNIN-OIDC/callback", true)]
    [InlineData("POST", Attacker, "cross-site", "/signin-oidcx", false)]
    public async Task Matrix(string method, string? origin, string? secFetchSite, string path, bool passes) {
        var pipeline = BuildPipeline(o => {
            o.AllowedOrigins.Add(AllowedOrigin);
            o.ExemptPathPrefixes.Add("/signin-oidc");
        });
        var context = CreateContext(method, path, origin is null ? StringValues.Empty : new StringValues(origin),
            secFetchSite);

        await pipeline(context);

        context.Response.StatusCode.Should().Be(passes ? StatusCodes.Status204NoContent : StatusCodes.Status403Forbidden);
    }

    [Theory]
    // A WebSocket handshake is a GET, but it is judged like an unsafe request (Cross-Site WebSocket Hijacking).
    [InlineData("websocket", Attacker, "cross-site", "/ws", false)]
    [InlineData("WebSocket", Attacker, "cross-site", "/ws", false)]
    [InlineData("foo, websocket", Attacker, "cross-site", "/ws", false)]
    [InlineData("websocket", Sibling, "same-site", "/ws", false)]
    [InlineData("websocket", Attacker, null, "/ws", false)]
    [InlineData("websocket", "null", null, "/ws", false)]
    [InlineData("websocket", OwnOrigin, "same-origin", "/ws", true)]
    [InlineData("websocket", OwnOrigin, null, "/ws", true)]
    [InlineData("websocket", AllowedOrigin, "cross-site", "/ws", true)]
    [InlineData("websocket", AllowedOrigin, null, "/ws", true)]
    // Neither header: a non-browser client (a device, a server) is not a hijacking vector.
    [InlineData("websocket", null, null, "/ws", true)]
    [InlineData("websocket", Attacker, "cross-site", "/signin-oidc", true)]
    // Plain GETs and other upgrade protocols stay safe.
    [InlineData(null, Attacker, "cross-site", "/ws", true)]
    [InlineData("h2c", Attacker, "cross-site", "/ws", true)]
    [InlineData("websocketx", Attacker, null, "/ws", true)]
    public async Task WebSocketHandshake_JudgedLikeAnUnsafeRequest(
        string? upgrade, string? origin, string? secFetchSite, string path, bool passes) {
        var pipeline = BuildPipeline(o => {
            o.AllowedOrigins.Add(AllowedOrigin);
            o.ExemptPathPrefixes.Add("/signin-oidc");
        });
        var context = CreateContext("GET", path, origin is null ? StringValues.Empty : new StringValues(origin),
            secFetchSite);
        if (upgrade is not null) {
            context.Request.Headers.Connection = "Upgrade";
            context.Request.Headers.Upgrade = upgrade;
        }

        await pipeline(context);

        context.Response.StatusCode.Should().Be(passes ? StatusCodes.Status204NoContent : StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task TwoOriginHeaders_Refused() {
        var pipeline = BuildPipeline(null);
        var context = CreateContext("POST", "/rpc", new StringValues(new[] { OwnOrigin, Attacker }), null);

        await pipeline(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task WithoutConfiguration_OwnOriginPasses_AndCrossSiteRefused() {
        var pipeline = BuildPipeline(null);
        var own = CreateContext("POST", "/rpc", OwnOrigin, null);
        var crossSite = CreateContext("POST", "/rpc", Attacker, "cross-site");

        await pipeline(own);
        await pipeline(crossSite);

        own.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        crossSite.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData(false)] // a host that never registered ProblemDetails (only /rpc, reflection off)
    [InlineData(true)] // AddProblemDetails/AddElarionHttpJson: the host's IProblemDetailsService writes it
    public async Task Refusal_IsProblemDetails_WithStableCode(bool problemDetailsService) {
        var pipeline = BuildPipeline(null);
        var context = CreateContext("POST", "/rpc", Attacker, "cross-site", problemDetailsService);

        await pipeline(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Response.ContentType.Should().StartWith("application/problem+json");
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(
            context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(403);
        doc.RootElement.GetProperty("code").GetString().Should().Be(CrossOriginProtectionErrorCodes.Refused);
    }

    [Theory]
    [InlineData("app.example.com")]
    [InlineData("https://app.example.com/path")]
    [InlineData("https://app.example.com?x=1")]
    [InlineData("https://user@app.example.com")]
    [InlineData("ftp://app.example.com")]
    [InlineData("null")]
    public void InvalidAllowedOrigin_RejectedAtStartup(string origin) {
        var act = () => BuildPipeline(o => o.AllowedOrigins.Add(origin));

        act.Should().Throw<ArgumentException>().WithMessage($"*{origin}*");
    }

    [Fact]
    public void RootExemptPrefix_RejectedAtStartup() {
        var act = () => BuildPipeline(o => o.ExemptPathPrefixes.Add("/"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task OverHttp_InFrontOfRpc_RefusesCrossSiteJson_AndPassesSameOriginAndServerCalls() {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.UseElarionCrossOriginProtection();
        app.MapPost("/rpc", (RequestDelegate)(context => {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return context.Response.WriteAsync("dispatched", context.RequestAborted);
        }));
        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            var crossSite = await client.SendAsync(Post(Attacker, "cross-site"), ct);
            var sameOrigin = await client.SendAsync(Post(baseAddress, "same-origin"), ct);
            var legacySameOrigin = await client.SendAsync(Post(baseAddress, null), ct);
            var server = await client.SendAsync(Post(null, null), ct);

            crossSite.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await crossSite.Content.ReadAsStringAsync(ct)).Should().Contain(CrossOriginProtectionErrorCodes.Refused);
            sameOrigin.StatusCode.Should().Be(HttpStatusCode.OK);
            legacySameOrigin.StatusCode.Should().Be(HttpStatusCode.OK);
            server.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally {
            await app.StopAsync(ct);
        }

        static HttpRequestMessage Post(string? origin, string? secFetchSite) {
            var request = new HttpRequestMessage(HttpMethod.Post, "/rpc") {
                Content = new StringContent("""{"jsonrpc":"2.0","method":"x","id":1}""", Encoding.UTF8,
                    "application/json")
            };
            if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin.TrimEnd('/'));
            if (secFetchSite is not null) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", secFetchSite);
            return request;
        }
    }

    [Fact]
    public async Task OverHttp_InFrontOfAWebSocketEndpoint_RefusesCrossSiteHandshake_AndAcceptsOthers() {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.UseElarionCrossOriginProtection(o => o.AllowedOrigins.Add(AllowedOrigin));
        app.UseWebSockets();
        app.MapGet("/ws", (RequestDelegate)(async context => {
            if (!context.WebSockets.IsWebSocketRequest) {
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsync("plain", context.RequestAborted);
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", context.RequestAborted);
        }));
        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
            var socketUri = new Uri(baseAddress.Replace("http://", "ws://") + "/ws");

            var crossSite = await ConnectAsync(socketUri, Attacker, "cross-site", ct);
            var legacyCrossSite = await ConnectAsync(socketUri, Attacker, null, ct);
            var sameOrigin = await ConnectAsync(socketUri, baseAddress, "same-origin", ct);
            var legacySameOrigin = await ConnectAsync(socketUri, baseAddress, null, ct);
            var allowed = await ConnectAsync(socketUri, AllowedOrigin, "cross-site", ct);
            var nonBrowser = await ConnectAsync(socketUri, null, null, ct);

            crossSite.Should().Be(HttpStatusCode.Forbidden);
            legacyCrossSite.Should().Be(HttpStatusCode.Forbidden);
            sameOrigin.Should().Be(HttpStatusCode.SwitchingProtocols);
            legacySameOrigin.Should().Be(HttpStatusCode.SwitchingProtocols);
            allowed.Should().Be(HttpStatusCode.SwitchingProtocols);
            nonBrowser.Should().Be(HttpStatusCode.SwitchingProtocols);

            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };
            using var plainGet = new HttpRequestMessage(HttpMethod.Get, "/ws");
            plainGet.Headers.TryAddWithoutValidation("Origin", Attacker);
            plainGet.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            var plain = await client.SendAsync(plainGet, ct);
            plain.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally {
            await app.StopAsync(ct);
        }

        static async Task<HttpStatusCode> ConnectAsync(
            Uri uri, string? origin, string? secFetchSite, CancellationToken ct) {
            using var socket = new ClientWebSocket();
            socket.Options.CollectHttpResponseDetails = true;
            if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
            if (secFetchSite is not null) socket.Options.SetRequestHeader("Sec-Fetch-Site", secFetchSite);
            try {
                await socket.ConnectAsync(uri, ct);
            }
            catch (WebSocketException) {
                return socket.HttpStatusCode;
            }

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
            return socket.HttpStatusCode;
        }
    }

    private static RequestDelegate BuildPipeline(Action<CrossOriginProtectionOptions>? configure) {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        var provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);
        app.UseElarionCrossOriginProtection(configure);
        app.Run(context => {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        return app.Build();
    }

    private static DefaultHttpContext CreateContext(string method, string path, StringValues origin,
        string? secFetchSite, bool problemDetailsService = false) {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        if (problemDetailsService) services.AddProblemDetails();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = method;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example.com");
        context.Request.Path = path;
        if (origin.Count > 0) context.Request.Headers.Origin = origin;
        if (secFetchSite is not null) context.Request.Headers["Sec-Fetch-Site"] = secFetchSite;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
