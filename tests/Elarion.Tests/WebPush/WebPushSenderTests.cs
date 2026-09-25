using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions.Identity;
using Elarion.Tests.Authorization;
using Elarion.WebPush;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.WebPush;

public sealed class WebPushSenderTests {
    private const string FcmEndpoint = "https://fcm.googleapis.com/fcm/send/device-1";
    private const string MozillaEndpoint = "https://updates.push.services.mozilla.com/wpush/v2/device-2";

    private static readonly WebPushMessage Message = new() {
        Title = "Deploy failed", Body = "api@4f2c1e failed its health check.", Url = "/deploys/4f2c1e"
    };

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Send_DeliversAnEncryptedVapidSignedMessageToEverySubscriptionOfTheUsers() {
        var pushService = new FakePushService();
        await using var provider = CreateProvider(pushService);
        using var phone = new TestPushSubscriber(FcmEndpoint);
        using var laptop = new TestPushSubscriber(MozillaEndpoint);
        using var stranger = new TestPushSubscriber("https://fcm.googleapis.com/fcm/send/other");
        var store = provider.GetRequiredService<IPushSubscriptionStore>();
        await store.UpsertAsync(phone.ToSubscription("alice"), TestToken);
        await store.UpsertAsync(laptop.ToSubscription("alice"), TestToken);
        await store.UpsertAsync(stranger.ToSubscription("bob"), TestToken);

        var result = await Send(provider, ["alice"], Message with { Tag = "deploy-4f2c1e", Urgency = WebPushUrgency.High });

        result.Should().Be(new WebPushResult { Attempted = 2, Delivered = 2 });
        pushService.Requests.Select(request => request.Uri.ToString()).Should().BeEquivalentTo([FcmEndpoint, MozillaEndpoint]);
        var request = pushService.Requests.Single(candidate => candidate.Uri.ToString() == FcmEndpoint);
        request.Method.Should().Be(HttpMethod.Post);
        request.Headers["Content-Encoding"].Should().Be("aes128gcm");
        request.Headers["TTL"].Should().Be("86400");
        request.Headers["Urgency"].Should().Be("high");
        request.Headers["Topic"].Should().Be("deploy-4f2c1e");
        var publicKey = (await provider.GetRequiredService<IVapidKeyProvider>().GetAsync(TestToken)).PublicKey;
        request.Headers["Authorization"].Should().StartWith("vapid t=").And.EndWith($", k={publicKey}");

        using var payload = JsonDocument.Parse(phone.Decrypt(request.Body));
        payload.RootElement.GetProperty("title").GetString().Should().Be("Deploy failed");
        payload.RootElement.GetProperty("body").GetString().Should().Be("api@4f2c1e failed its health check.");
        payload.RootElement.GetProperty("url").GetString().Should().Be("/deploys/4f2c1e");
        payload.RootElement.GetProperty("tag").GetString().Should().Be("deploy-4f2c1e");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task Send_GoneSubscription_IsRemoved(HttpStatusCode status) {
        var pushService = new FakePushService();
        pushService.Respond(FcmEndpoint, status);
        await using var provider = CreateProvider(pushService);
        using var gone = new TestPushSubscriber(FcmEndpoint);
        using var live = new TestPushSubscriber(MozillaEndpoint);
        var store = provider.GetRequiredService<IPushSubscriptionStore>();
        await store.UpsertAsync(gone.ToSubscription("alice"), TestToken);
        await store.UpsertAsync(live.ToSubscription("alice"), TestToken);

        var result = await Send(provider, ["alice"], Message);

        result.Should().Be(new WebPushResult { Attempted = 2, Delivered = 1, Removed = 1 });
        (await store.ListByUsersAsync(["alice"], TestToken)).Select(subscription => subscription.Endpoint)
            .Should().Equal(MozillaEndpoint);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Send_TransientOrUnexplainedFailure_KeepsTheSubscription(HttpStatusCode status) {
        var pushService = new FakePushService();
        pushService.Respond(FcmEndpoint, status);
        await using var provider = CreateProvider(pushService);
        using var subscriber = new TestPushSubscriber(FcmEndpoint);
        var store = provider.GetRequiredService<IPushSubscriptionStore>();
        await store.UpsertAsync(subscriber.ToSubscription("alice"), TestToken);

        var result = await Send(provider, ["alice"], Message);

        result.Should().Be(new WebPushResult { Attempted = 1, Failed = 1 });
        (await store.ListByUsersAsync(["alice"], TestToken)).Should().ContainSingle();
    }

    [Fact]
    public async Task Send_NetworkFailure_KeepsTheSubscription() {
        var pushService = new FakePushService();
        pushService.Throw(FcmEndpoint);
        await using var provider = CreateProvider(pushService);
        using var subscriber = new TestPushSubscriber(FcmEndpoint);
        var store = provider.GetRequiredService<IPushSubscriptionStore>();
        await store.UpsertAsync(subscriber.ToSubscription("alice"), TestToken);

        var result = await Send(provider, ["alice"], Message);

        result.Should().Be(new WebPushResult { Attempted = 1, Failed = 1 });
        (await store.ListByUsersAsync(["alice"], TestToken)).Should().ContainSingle();
    }

    [Fact]
    public async Task Send_MalformedKeysOrDisallowedEndpoint_IsRemovedWithoutARequest() {
        var pushService = new FakePushService();
        await using var provider = CreateProvider(pushService);
        using var subscriber = new TestPushSubscriber(FcmEndpoint);
        var store = provider.GetRequiredService<IPushSubscriptionStore>();
        await store.UpsertAsync(subscriber.ToSubscription("alice") with { P256dh = "bm90LWEta2V5" }, TestToken);
        await store.UpsertAsync(subscriber.ToSubscription("alice") with {
            Endpoint = "https://169.254.169.254/latest/meta-data"
        }, TestToken);

        var result = await Send(provider, ["alice"], Message);

        result.Should().Be(new WebPushResult { Attempted = 2, Removed = 2 });
        pushService.Requests.Should().BeEmpty();
        (await store.ListByUsersAsync(["alice"], TestToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Send_LongTag_IsHashedIntoAValidStableTopic() {
        var pushService = new FakePushService();
        await using var provider = CreateProvider(pushService);
        using var subscriber = new TestPushSubscriber(FcmEndpoint);
        await provider.GetRequiredService<IPushSubscriptionStore>().UpsertAsync(subscriber.ToSubscription("alice"), TestToken);
        var tag = "workspace:42/starter feeding reminder (Sauerteig)";

        await Send(provider, ["alice"], Message with { Tag = tag, TimeToLive = TimeSpan.Zero });
        await Send(provider, ["alice"], Message with { Tag = tag });

        var topics = pushService.Requests.Select(request => request.Headers["Topic"]).ToArray();
        topics[0].Should().MatchRegex("^[A-Za-z0-9_-]{32}$");
        topics[1].Should().Be(topics[0]);
        pushService.Requests.First().Headers["TTL"].Should().Be("0");
    }

    [Fact]
    public async Task Send_OversizedMessage_ThrowsBeforeSending() {
        var pushService = new FakePushService();
        await using var provider = CreateProvider(pushService);

        await FluentActions.Awaiting(() => Send(provider, ["alice"], Message with { Body = new string('x', 4000) }))
            .Should().ThrowAsync<ArgumentException>();
        pushService.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendToCurrentUser_SendsToTheCallersSubscriptionsOnly() {
        var pushService = new FakePushService();
        await using var provider = CreateProvider(pushService, new FakeCurrentUser { UserId = "alice", IsAuthenticated = true });
        using var mine = new TestPushSubscriber(FcmEndpoint);
        using var theirs = new TestPushSubscriber(MozillaEndpoint);
        var store = provider.GetRequiredService<IPushSubscriptionStore>();
        await store.UpsertAsync(mine.ToSubscription("alice"), TestToken);
        await store.UpsertAsync(theirs.ToSubscription("bob"), TestToken);

        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IWebPushSender>()
            .SendToCurrentUserAsync(Message, TestToken);

        result.Delivered.Should().Be(1);
        pushService.Requests.Single().Uri.ToString().Should().Be(FcmEndpoint);
    }

    [Fact]
    public async Task SendToCurrentUser_Unauthenticated_SendsNothing() {
        var pushService = new FakePushService();
        await using var provider = CreateProvider(pushService, new FakeCurrentUser { UserId = "alice" });
        using var subscriber = new TestPushSubscriber(FcmEndpoint);
        await provider.GetRequiredService<IPushSubscriptionStore>().UpsertAsync(subscriber.ToSubscription("alice"), TestToken);

        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IWebPushSender>()
            .SendToCurrentUserAsync(Message, TestToken);

        result.Should().Be(WebPushResult.Empty);
        pushService.Requests.Should().BeEmpty();
    }

    internal static ServiceProvider CreateProvider(FakePushService pushService, ICurrentUser? currentUser = null) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionWebPush(options => options.Subject = "mailto:ops@example.com");
        services.AddHttpClient(WebPushOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(pushService.CreateHandler);
        if (currentUser is not null) services.AddScoped(_ => currentUser);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task<WebPushResult> Send(ServiceProvider provider, string[] userIds, WebPushMessage message) {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWebPushSender>().SendToUsersAsync(userIds, message, TestToken);
    }
}
