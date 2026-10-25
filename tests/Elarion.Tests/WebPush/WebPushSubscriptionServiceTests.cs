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
    public async Task Subscribe_AnotherUsersEndpointWithoutItsAuthSecret_IsRefusedAndChangesNothing() {
        await using var provider = CreateProvider();
        using var victim = new TestPushSubscriber(Endpoint);
        using var attacker = new TestPushSubscriber(Endpoint);
        await Service(provider, "alice").SubscribeAsync(victim.ToRequest(), cancellationToken: TestToken);

        var result = await Service(provider, "mallory").SubscribeAsync(attacker.ToRequest(), cancellationToken: TestToken);

        // The same kind as any invalid subscription: nothing says the endpoint is someone else's.
        result.Error.Kind.Should().Be(ErrorKind.Validation);
        (await Store(provider).ListByUsersAsync(["mallory"], TestToken)).Should().BeEmpty();
        var kept = (await Store(provider).ListByUsersAsync(["alice"], TestToken)).Single();
        kept.P256dh.Should().Be(victim.P256dh);
        kept.Auth.Should().Be(victim.Auth);
    }

    [Fact]
    public async Task Subscribe_SameOwnerWithNewKeys_RefreshesTheSubscription() {
        await using var provider = CreateProvider();
        using var original = new TestPushSubscriber(Endpoint);
        using var rotated = new TestPushSubscriber(Endpoint);
        await Service(provider, "alice").SubscribeAsync(original.ToRequest(), cancellationToken: TestToken);

        var result = await Service(provider, "alice").SubscribeAsync(rotated.ToRequest(), cancellationToken: TestToken);

        result.IsSuccess.Should().BeTrue();
        var stored = (await Store(provider).ListByUsersAsync(["alice"], TestToken)).Single();
        stored.P256dh.Should().Be(rotated.P256dh);
        stored.Auth.Should().Be(rotated.Auth);
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
        (await store.UpsertAsync(subscriber.ToSubscription("alice") with { CreatedAt = created, LastSeenAt = created }, TestToken))
            .Should().BeTrue();

        (await store.UpsertAsync(subscriber.ToSubscription("alice") with { CreatedAt = later, LastSeenAt = later }, TestToken))
            .Should().BeTrue();
        var refreshed = (await store.ListByUsersAsync(["alice"], TestToken)).Single();
        (await store.UpsertAsync(subscriber.ToSubscription("bob") with { CreatedAt = later, LastSeenAt = later }, TestToken))
            .Should().BeTrue();
        var reassigned = (await store.ListByUsersAsync(["bob"], TestToken)).Single();

        refreshed.CreatedAt.Should().Be(created);
        refreshed.LastSeenAt.Should().Be(later);
        reassigned.CreatedAt.Should().Be(later);
    }

    [Fact]
    public async Task InMemoryStore_AnotherUserWithoutTheAuthSecret_WritesNothing() {
        var store = new InMemoryPushSubscriptionStore();
        using var victim = new TestPushSubscriber(Endpoint);
        using var attacker = new TestPushSubscriber(Endpoint);
        await store.UpsertAsync(victim.ToSubscription("alice"), TestToken);

        (await store.UpsertAsync(attacker.ToSubscription("mallory") with { LastSeenAt = DateTimeOffset.UnixEpoch.AddDays(1) }, TestToken))
            .Should().BeFalse();

        (await store.ListByUsersAsync(["alice", "mallory"], TestToken)).Should().Equal(victim.ToSubscription("alice"));
    }

    [Fact]
    public async Task InMemoryStore_ConcurrentSubscribesFromDifferentUsers_LeaveExactlyOneOwner() {
        var store = new InMemoryPushSubscriptionStore();
        var subscribers = Enumerable.Range(0, 8).Select(_ => new TestPushSubscriber(Endpoint)).ToArray();
        try {
            using var start = new Barrier(subscribers.Length);
            var written = await Task.WhenAll(subscribers.Select((subscriber, i) => Task.Run(() => {
                start.SignalAndWait(TestToken);
                return store.UpsertAsync(subscriber.ToSubscription($"user-{i}"), TestToken).AsTask();
            }, TestToken)));

            written.Count(w => w).Should().Be(1);
            var winner = Array.IndexOf(written, true);
            (await store.ListByUsersAsync(subscribers.Select((_, i) => $"user-{i}").ToArray(), TestToken))
                .Should().Equal(subscribers[winner].ToSubscription($"user-{winner}"));
        }
        finally {
            foreach (var subscriber in subscribers) subscriber.Dispose();
        }
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

    [Fact]
    public async Task InMemoryStore_ListSubscribedUserIds_ReturnsDistinctOwnersSorted() {
        var store = new InMemoryPushSubscriptionStore();
        using var phone = new TestPushSubscriber(Endpoint);
        using var laptop = new TestPushSubscriber("https://fcm.googleapis.com/fcm/send/device-2");
        using var tablet = new TestPushSubscriber("https://fcm.googleapis.com/fcm/send/device-3");
        await store.UpsertAsync(phone.ToSubscription("bob"), TestToken);
        await store.UpsertAsync(laptop.ToSubscription("bob"), TestToken);
        await store.UpsertAsync(tablet.ToSubscription("alice"), TestToken);

        (await store.ListSubscribedUserIdsAsync(cancellationToken: TestToken)).Should().Equal("alice", "bob");
        (await store.ListSubscribedUserIdsAsync(["carol", "bob"], TestToken)).Should().Equal("bob");
        (await store.ListSubscribedUserIdsAsync([], TestToken)).Should().BeEmpty();
    }
}
