using System.Net;
using AwesomeAssertions;
using Elarion.WebPush;
using Elarion.WebPush.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Elarion.Tests.WebPush;

/// <summary>A database the EF Web Push stores run against in the shared store tests.</summary>
public interface IWebPushStoreFixture<out TContext> where TContext : DbContext {
    bool IsAvailable { get; }

    string SkipReason { get; }

    /// <summary>Points a context at this database (the stores resolve their context from DI).</summary>
    void Configure(DbContextOptionsBuilder options);

    TContext CreateContext();
}

/// <summary>
/// The contract of the EF-backed <see cref="IPushSubscriptionStore"/> and <see cref="IVapidKeyStore"/>, run once
/// per supported provider: the raw upsert/insert statements must behave the same on each, and the subscription
/// store must work inside the caller's unit of work on each.
/// </summary>
public abstract class WebPushStoreTestBase<TContext>(IWebPushStoreFixture<TContext> fixture) where TContext : DbContext {
    protected static CancellationToken TestToken => TestContext.Current.CancellationToken;

    protected IWebPushStoreFixture<TContext> Fixture => fixture;

    [Fact]
    public async Task SubscriptionStore_UpsertListRemove_Roundtrips() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>();
        var user = NewId();
        using var subscriber = new TestPushSubscriber(NewEndpoint());
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        await store.UpsertAsync(subscriber.ToSubscription(user) with {
            UserAgent = null, CreatedAt = now, LastSeenAt = now
        }, TestToken);

        var stored = (await store.ListByUsersAsync([user, NewId()], TestToken)).Single();
        stored.Endpoint.Should().Be(subscriber.Endpoint);
        stored.P256dh.Should().Be(subscriber.P256dh);
        stored.Auth.Should().Be(subscriber.Auth);
        stored.UserAgent.Should().BeNull();
        stored.CreatedAt.Should().Be(now);

        (await store.RemoveAsync(subscriber.Endpoint, cancellationToken: TestToken)).Should().BeTrue();
        (await store.ListByUsersAsync([user], TestToken)).Should().BeEmpty();
        (await store.RemoveAsync(subscriber.Endpoint, cancellationToken: TestToken)).Should().BeFalse();
    }

    [Fact]
    public async Task SubscriptionStore_EmptyUserAgent_IsStoredAsNull() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>();
        var user = NewId();
        using var subscriber = new TestPushSubscriber(NewEndpoint());

        await store.UpsertAsync(subscriber.ToSubscription(user) with { UserAgent = "" }, TestToken);

        (await store.ListByUsersAsync([user], TestToken)).Single().UserAgent.Should().BeNull();
    }

    [Fact]
    public async Task SubscriptionStore_ResubscribeUnderAnotherUser_ReassignsTheOneRow() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>();
        var (alice, bob) = (NewId(), NewId());
        using var subscriber = new TestPushSubscriber(NewEndpoint());
        var first = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var refresh = first.AddDays(1);
        var reassign = first.AddDays(2);

        await store.UpsertAsync(subscriber.ToSubscription(alice) with { CreatedAt = first, LastSeenAt = first }, TestToken);
        await store.UpsertAsync(subscriber.ToSubscription(alice) with {
            UserAgent = "Firefox", CreatedAt = refresh, LastSeenAt = refresh
        }, TestToken);
        var refreshed = (await store.ListByUsersAsync([alice], TestToken)).Single();
        await store.UpsertAsync(subscriber.ToSubscription(bob) with { CreatedAt = reassign, LastSeenAt = reassign }, TestToken);

        refreshed.CreatedAt.Should().Be(first);
        refreshed.LastSeenAt.Should().Be(refresh);
        refreshed.UserAgent.Should().Be("Firefox");
        (await store.ListByUsersAsync([alice], TestToken)).Should().BeEmpty();
        var reassigned = (await store.ListByUsersAsync([bob], TestToken)).Single();
        reassigned.CreatedAt.Should().Be(reassign);
        await using var context = fixture.CreateContext();
        (await context.Set<PushSubscriptionEntity>().CountAsync(entity => entity.Endpoint == subscriber.Endpoint, TestToken))
            .Should().Be(1);
    }

    [Fact]
    public async Task SubscriptionStore_RemoveWithOwner_LeavesOtherUsersSubscription() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>();
        var owner = NewId();
        using var subscriber = new TestPushSubscriber(NewEndpoint());
        await store.UpsertAsync(subscriber.ToSubscription(owner), TestToken);

        (await store.RemoveAsync(subscriber.Endpoint, NewId(), TestToken)).Should().BeFalse();
        (await store.RemoveAsync(subscriber.Endpoint, owner, TestToken)).Should().BeTrue();
    }

    [Fact]
    public async Task VapidKeyStore_ConcurrentFirstUse_AllNodesAdoptOneWinner() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using (var context = fixture.CreateContext()) {
            await context.Set<VapidKeyEntity>().ExecuteDeleteAsync(TestToken);
        }

        // Independent providers stand in for nodes: each resolves through its own key provider.
        var providers = Enumerable.Range(0, 6).Select(_ => CreateProvider()).ToArray();
        try {
            var keys = await Task.WhenAll(providers.Select(provider =>
                provider.GetRequiredService<IVapidKeyProvider>().GetAsync(TestToken).AsTask()));

            keys.Distinct().Should().ContainSingle();
            (await providers[0].GetRequiredService<IVapidKeyStore>().GetAsync(TestToken)).Should().Be(keys[0]);
        }
        finally {
            foreach (var provider in providers) await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task SubscriptionStore_InsideTheCallersTransaction_CommitsAndRollsBackWithIt() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var user = NewId();
        using var discarded = new TestPushSubscriber(NewEndpoint());
        using var kept = new TestPushSubscriber(NewEndpoint());

        // What a command handler does under the transaction decorator: the unit of work opens a transaction on
        // the scope's context, and the handler subscribes through the store. On SQLite that transaction holds the
        // only write lock, so a store writing on a connection of its own would wait for it and time out.
        foreach (var (subscriber, commit) in new[] { (discarded, false), (kept, true) }) {
            await using var scope = provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(TestToken);
            var store = scope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>();

            await store.UpsertAsync(subscriber.ToSubscription(user), TestToken);
            (await store.ListByUsersAsync([user], TestToken)).Should().Contain(s => s.Endpoint == subscriber.Endpoint);

            if (commit) await transaction.CommitAsync(TestToken);
            else await transaction.RollbackAsync(TestToken);
        }

        await using var verify = provider.CreateAsyncScope();
        (await verify.ServiceProvider.GetRequiredService<IPushSubscriptionStore>().ListByUsersAsync([user], TestToken))
            .Select(s => s.Endpoint).Should().Equal(kept.Endpoint);
    }

    [Fact]
    public async Task Sender_ManyDeadSubscriptions_AreAllRemovedInOneSend() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var pushService = new FakePushService();
        await using var provider = CreateProvider(pushService);
        var user = NewId();
        var subscribers = Enumerable.Range(0, 6).Select(_ => new TestPushSubscriber(NewEndpoint())).ToList();
        try {
            await using var scope = provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>();
            foreach (var subscriber in subscribers) {
                pushService.Respond(subscriber.Endpoint, HttpStatusCode.Gone);
                await store.UpsertAsync(subscriber.ToSubscription(user), TestToken);
            }

            // The deliveries run in parallel; the removals share the scope's one context, which takes one
            // operation at a time.
            var result = await scope.ServiceProvider.GetRequiredService<IWebPushSender>().SendToUsersAsync(
                [user], new WebPushMessage { Title = "Gone", Body = "Every endpoint answers 410." }, TestToken);

            result.Should().Be(new WebPushResult { Attempted = 6, Removed = 6 });
            (await store.ListByUsersAsync([user], TestToken)).Should().BeEmpty();
        }
        finally {
            foreach (var subscriber in subscribers) subscriber.Dispose();
        }
    }

    [Fact]
    public async Task VapidKeyPair_IsResolvedWhenTheHostStarts() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using (var context = fixture.CreateContext()) {
            await context.Set<VapidKeyEntity>().ExecuteDeleteAsync(TestToken);
        }

        await using var provider = CreateProvider();
        foreach (var hosted in provider.GetServices<IHostedService>()) await hosted.StartAsync(TestToken);

        // Stored before any request could first need it — and so never inside a request's transaction.
        (await provider.GetRequiredService<IVapidKeyStore>().GetAsync(TestToken)).Should().NotBeNull();
    }

    private protected ServiceProvider CreateProvider(FakePushService? pushService = null) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TContext>(fixture.Configure);
        services.AddElarionWebPushEntityFrameworkCore<TContext>(options => options.Subject = "mailto:ops@example.com");
        if (pushService is not null)
            services.AddHttpClient(WebPushOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(pushService.CreateHandler);
        // Scope validation: the subscription store is scoped (it is the caller's context), and nothing that
        // outlives a scope may hold it.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    protected static string NewId() {
        return Guid.CreateVersion7().ToString("N");
    }

    protected static string NewEndpoint() {
        return $"https://fcm.googleapis.com/fcm/send/{NewId()}";
    }
}
