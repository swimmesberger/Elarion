using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions.Identity;
using Elarion.Tests.Authorization;
using Elarion.WebPush;
using Elarion.WebPush.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.WebPush;

/// <summary>
/// The endpoints the browser package's default transport calls, over a real Kestrel host: the wire shapes
/// (<c>subscription.toJSON()</c> in, <c>{publicKey}</c> out) and the bodyless status contract.
/// </summary>
public sealed class WebPushEndpointsTests {
    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/device-1";

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PublicKey_ReturnsTheVapidPublicKey() {
        await using var host = await StartAsync();

        using var response = JsonDocument.Parse(await host.Client.GetStringAsync("/webpush/public-key", TestToken));

        var expected = (await host.App.Services.GetRequiredService<IVapidKeyProvider>().GetAsync(TestToken)).PublicKey;
        response.RootElement.GetProperty("publicKey").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task Subscribe_ThenUnsubscribe_RoundTripsTheBrowserSubscriptionJson() {
        await using var host = await StartAsync();
        using var subscriber = new TestPushSubscriber(Endpoint);
        var store = host.App.Services.GetRequiredService<IPushSubscriptionStore>();
        // Exactly what PushSubscription.toJSON() produces in a browser.
        var json = $$$"""{"endpoint":"{{{Endpoint}}}","expirationTime":null,"keys":{"p256dh":"{{{subscriber.P256dh}}}","auth":"{{{subscriber.Auth}}}"}}""";

        var subscribe = await host.Client.PostAsync("/webpush/subscribe", Json(json), TestToken);

        subscribe.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var stored = (await store.ListByUsersAsync(["alice"], TestToken)).Single();
        stored.P256dh.Should().Be(subscriber.P256dh);
        stored.UserAgent.Should().Be("elarion-tests/1.0");

        var unsubscribe = await host.Client.PostAsync("/webpush/unsubscribe", Json($$"""{"endpoint":"{{Endpoint}}"}"""), TestToken);

        unsubscribe.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await store.ListByUsersAsync(["alice"], TestToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Subscribe_Unauthenticated_Returns401() {
        await using var host = await StartAsync(new FakeCurrentUser { UserId = "alice" });
        using var subscriber = new TestPushSubscriber(Endpoint);

        var response = await host.Client.PostAsync("/webpush/subscribe", Json(
            $$$"""{"endpoint":"{{{Endpoint}}}","keys":{"p256dh":"{{{subscriber.P256dh}}}","auth":"{{{subscriber.Auth}}}"}}"""), TestToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("""{"endpoint":"https://internal.example/hook","keys":{"p256dh":"x","auth":"y"}}""")]
    [InlineData("""{"endpoint":""")]
    [InlineData("""[]""")]
    public async Task Subscribe_InvalidBody_Returns400(string body) {
        await using var host = await StartAsync();

        var response = await host.Client.PostAsync("/webpush/subscribe", Json(body), TestToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static StringContent Json(string json) {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<TestHost> StartAsync(ICurrentUser? user = null) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddScoped(_ => user ?? new FakeCurrentUser { UserId = "alice", IsAuthenticated = true });
        builder.Services.AddElarionWebPush(options => options.Subject = "mailto:ops@example.com");

        var app = builder.Build();
        app.MapElarionWebPush();
        await app.StartAsync(TestToken);

        var baseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(baseAddress) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("elarion-tests/1.0");
        return new TestHost(app, client);
    }

    private sealed class TestHost(WebApplication app, HttpClient client) : IAsyncDisposable {
        public WebApplication App { get; } = app;

        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync() {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}
