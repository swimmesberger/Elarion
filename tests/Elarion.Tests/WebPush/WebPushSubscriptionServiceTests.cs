using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Elarion.Tests.Authorization;
using Elarion.WebPush;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.WebPush;

public sealed class WebPushSubscriptionServiceTests {
    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/device-1";

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Subscribe_StoresTheSubscriptionForTheCurrentUser() {
        await using var provider = CreateProvider();
        using var subscriber = new TestPushSubscriber(Endpoint);

        var result = await Service(provider, "alice").SubscribeAsync(subscriber.ToRequest(), "Mozilla/5.0", TestToken);

        result.IsSuccess.Should().BeTrue();
        var stored = (await Store(provider).ListByUsersAsync(["alice"], TestToken)).Single();
        stored.Endpoint.Should().Be(Endpoint);
        stored.P256dh.Should().Be(subscriber.P256dh);
        stored.Auth.Should().Be(subscriber.Auth);
        stored.UserAgent.Should().Be("Mozilla/5.0");
    }

    [Fact]
    public async Task Subscribe_SameDeviceUnderAnotherAccount_ReassignsTheSubscription() {
        await using var provider = CreateProvider();
        using var subscriber = new TestPushSubscriber(Endpoint);
        await Service(provider, "alice").SubscribeAsync(subscriber.ToRequest(), cancellationToken: TestToken);

        var result = await Service(provider, "bob").SubscribeAsync(subscriber.ToRequest(), cancellationToken: TestToken);

        result.IsSuccess.Should().BeTrue();
        (await Store(provider).ListByUsersAsync(["alice"], TestToken)).Should().BeEmpty();
        (await Store(provider).ListByUsersAsync(["bob"], TestToken)).Should().ContainSingle();
    }

    [Fact]
    public async Task Subscribe_Unauthenticated_IsUnauthorized() {
        await using var provider = CreateProvider();
        using var subscriber = new TestPushSubscriber(Endpoint);
        await using var scope = provider.CreateAsyncScope();

        var result = await ServiceFor(scope, new FakeCurrentUser { UserId = "alice" })
            .SubscribeAsync(subscriber.ToRequest(), cancellationToken: TestToken);

        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
        (await Store(provider).ListByUsersAsync(["alice"], TestToken)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://fcm.googleapis.com/fcm/send/x")]
    [InlineData("/relative")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://internal.example/hook")]
    public async Task Subscribe_EndpointOutsideTheAllowedPushServices_IsRejected(string endpoint) {
        await using var provider = CreateProvider();
        using var subscriber = new TestPushSubscriber(endpoint);

        var result = await Service(provider, "alice").SubscribeAsync(subscriber.ToRequest(), cancellationToken: TestToken);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task Subscribe_SelfHostedPushServiceAdded_IsAccepted() {
        await using var provider = CreateProvider(options => options.AllowedEndpointHosts.Add("push.example.net"));
        using var subscriber = new TestPushSubscriber("https://push.example.net/push/abc");

        var result = await Service(provider, "alice").SubscribeAsync(subscriber.ToRequest(), cancellationToken: TestToken);

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("not base64url!", null)]
    [InlineData("AAAA", null)]
    [InlineData(null, "AAAA")]
    public async Task Subscribe_MalformedKeys_AreRejected(string? p256dh, string? auth) {
        await using var provider = CreateProvider();
        using var subscriber = new TestPushSubscriber(Endpoint);
        var request = subscriber.ToRequest();
        request = request with {
            Keys = new PushSubscriptionKeys { P256dh = p256dh ?? request.Keys.P256dh, Auth = auth ?? request.Keys.Auth }
        };

        var result = await Service(provider, "alice").SubscribeAsync(request, cancellationToken: TestToken);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task Unsubscribe_RemovesOnlyTheCallersOwnSubscription() {
        await using var provider = CreateProvider();
        using var subscriber = new TestPushSubscriber(Endpoint);
        await Service(provider, "alice").SubscribeAsync(subscriber.ToRequest(), cancellationToken: TestToken);

        (await Service(provider, "mallory").UnsubscribeAsync(Endpoint, TestToken)).IsSuccess.Should().BeTrue();
        (await Store(provider).ListByUsersAsync(["alice"], TestToken)).Should().ContainSingle();

        (await Service(provider, "alice").UnsubscribeAsync(Endpoint, TestToken)).IsSuccess.Should().BeTrue();
        (await Store(provider).ListByUsersAsync(["alice"], TestToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetPublicKey_ReturnsTheStableVapidPublicKey() {
        await using var provider = CreateProvider();

        var first = await Service(provider, "alice").GetPublicKeyAsync(TestToken);

        (await Service(provider, "bob").GetPublicKeyAsync(TestToken)).Should().Be(first);
        (await provider.GetRequiredService<IVapidKeyProvider>().GetAsync(TestToken)).PublicKey.Should().Be(first);
    }

    [Fact]
    public async Task InMemoryStore_ReassignmentRestartsCreatedAtAndRefreshKeepsIt() {
        var store = new InMemoryPushSubscriptionStore();
        using var subscriber = new TestPushSubscriber(Endpoint);
        var created = DateTimeOffset.UnixEpoch.AddDays(1);
        var later = created.AddDays(1);
        await store.UpsertAsync(subscriber.ToSubscription("alice") with { CreatedAt = created, LastSeenAt = created }, TestToken);

        await store.UpsertAsync(subscriber.ToSubscription("alice") with { CreatedAt = later, LastSeenAt = later }, TestToken);
        var refreshed = (await store.ListByUsersAsync(["alice"], TestToken)).Single();
        await store.UpsertAsync(subscriber.ToSubscription("bob") with { CreatedAt = later, LastSeenAt = later }, TestToken);
        var reassigned = (await store.ListByUsersAsync(["bob"], TestToken)).Single();

        refreshed.CreatedAt.Should().Be(created);
        refreshed.LastSeenAt.Should().Be(later);
        reassigned.CreatedAt.Should().Be(later);
    }

    private static ServiceProvider CreateProvider(Action<WebPushOptions>? configure = null) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionWebPush(options => {
            options.Subject = "mailto:ops@example.com";
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static IPushSubscriptionStore Store(ServiceProvider provider) {
        return provider.GetRequiredService<IPushSubscriptionStore>();
    }

    private static WebPushSubscriptionService Service(ServiceProvider provider, string userId) {
        return ServiceFor(provider, new FakeCurrentUser { UserId = userId, IsAuthenticated = true });
    }

    private static WebPushSubscriptionService ServiceFor(IServiceProvider provider, ICurrentUser currentUser) {
        return ActivatorUtilities.CreateInstance<WebPushSubscriptionService>(provider, currentUser);
    }

    private static WebPushSubscriptionService ServiceFor(AsyncServiceScope scope, ICurrentUser currentUser) {
        return ServiceFor(scope.ServiceProvider, currentUser);
    }
}
