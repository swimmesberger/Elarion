using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Results;
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
/// (<c>subscription.toJSON()</c> in, <c>{publicKey}</c> out), the bodyless status contract, and — the point of
/// dispatching to application handlers — that whatever wraps those handlers (here a stand-in for the generated
/// decorator pipeline) decides every call.
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

    [Fact]
    public async Task Subscribe_GoesThroughTheHandlerPipeline_SoItsDenialsApply() {
        var pipeline = new PipelineProbe { Denial = AppError.Forbidden("Not in this realm.") };
        await using var host = await StartAsync(pipeline: pipeline);
        using var subscriber = new TestPushSubscriber(Endpoint);

        var response = await host.Client.PostAsync("/webpush/subscribe", Json(
            $$$"""{"endpoint":"{{{Endpoint}}}","keys":{"p256dh":"{{{subscriber.P256dh}}}","auth":"{{{subscriber.Auth}}}"}}"""), TestToken);
        var unsubscribe = await host.Client.PostAsync("/webpush/unsubscribe", Json($$"""{"endpoint":"{{Endpoint}}"}"""), TestToken);
        var publicKey = await host.Client.GetAsync("/webpush/public-key", TestToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        unsubscribe.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        publicKey.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        pipeline.Calls.Should().Equal("subscribe", "unsubscribe", "publicKey");
        (await host.App.Services.GetRequiredService<IPushSubscriptionStore>().ListByUsersAsync(["alice"], TestToken))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, HttpStatusCode.Unauthorized)]
    [InlineData(false, HttpStatusCode.BadRequest)]
    public async Task Subscribe_MalformedBody_IsAnsweredByTheHandlersGateBeforeItIsCalledInvalid(bool denied, HttpStatusCode expected) {
        var pipeline = new PipelineProbe { Gate = denied ? AppError.Unauthorized("Sign in.") : null };
        await using var host = await StartAsync(pipeline: pipeline);

        var response = await host.Client.PostAsync("/webpush/subscribe", Json("""{"endpoint":"""), TestToken);

        response.StatusCode.Should().Be(expected);
        pipeline.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task MapElarionWebPush_WithoutTheApplicationHandlers_FailsAtStartupNamingThem() {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddElarionWebPush(options => options.Subject = "mailto:ops@example.com");
        await using var app = builder.Build();

        var act = () => app.MapElarionWebPush();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PushSubscriptionRequest*WebPushUnsubscribeRequest*WebPushPublicKeyRequest*");
    }

    private static StringContent Json(string json) {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<TestHost> StartAsync(ICurrentUser? user = null, PipelineProbe? pipeline = null) {
        pipeline ??= new PipelineProbe();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddScoped(_ => user ?? new FakeCurrentUser { UserId = "alice", IsAuthenticated = true });
        builder.Services.AddElarionWebPush(options => options.Subject = "mailto:ops@example.com");
        // What the application declares: three [Handler]s delegating to the service. The probe stands in for the
        // decorators the generator wraps around them (authorization, audit, rate limiting).
        builder.Services.AddSingleton(pipeline);
        builder.Services.AddScoped<IHandler<PushSubscriptionRequest, Result<Unit>>, SubscribeHandler>();
        builder.Services.AddScoped<IHandler<WebPushUnsubscribeRequest, Result<Unit>>, UnsubscribeHandler>();
        builder.Services.AddScoped<IHandler<WebPushPublicKeyRequest, Result<WebPushPublicKeyResponse>>, PublicKeyHandler>();
        builder.Services.AddKeyedSingleton<IHandlerGate>(typeof(PushSubscriptionRequest), new ProbeGate(pipeline));

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

    private sealed class PipelineProbe {
        public List<string> Calls { get; } = [];

        public AppError? Denial { get; init; }

        public AppError? Gate { get; init; }
    }

    private sealed class ProbeGate(PipelineProbe probe) : IHandlerGate {
        public ValueTask<AppError?> EvaluateAsync(IServiceProvider scope, CancellationToken ct) {
            return ValueTask.FromResult(probe.Gate);
        }
    }

    private sealed class SubscribeHandler(WebPushSubscriptionService subscriptions, PipelineProbe probe)
        : IHandler<PushSubscriptionRequest, Result<Unit>> {
        public async ValueTask<Result<Unit>> HandleAsync(PushSubscriptionRequest request, CancellationToken ct) {
            probe.Calls.Add("subscribe");
            if (probe.Denial is { } denial) return Result<Unit>.Failure(denial);
            return (await subscriptions.SubscribeAsync(request, cancellationToken: ct)).ToResultUnit();
        }
    }

    private sealed class UnsubscribeHandler(WebPushSubscriptionService subscriptions, PipelineProbe probe)
        : IHandler<WebPushUnsubscribeRequest, Result<Unit>> {
        public async ValueTask<Result<Unit>> HandleAsync(WebPushUnsubscribeRequest request, CancellationToken ct) {
            probe.Calls.Add("unsubscribe");
            if (probe.Denial is { } denial) return Result<Unit>.Failure(denial);
            return (await subscriptions.UnsubscribeAsync(request.Endpoint, ct)).ToResultUnit();
        }
    }

    private sealed class PublicKeyHandler(WebPushSubscriptionService subscriptions, PipelineProbe probe)
        : IHandler<WebPushPublicKeyRequest, Result<WebPushPublicKeyResponse>> {
        public async ValueTask<Result<WebPushPublicKeyResponse>> HandleAsync(WebPushPublicKeyRequest request, CancellationToken ct) {
            probe.Calls.Add("publicKey");
            if (probe.Denial is { } denial) return Result<WebPushPublicKeyResponse>.Failure(denial);
            return new WebPushPublicKeyResponse(await subscriptions.GetPublicKeyAsync(ct));
        }
    }
}
