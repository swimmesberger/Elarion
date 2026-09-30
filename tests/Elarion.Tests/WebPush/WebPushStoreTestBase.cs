using AwesomeAssertions;
using Elarion.WebPush;
using Elarion.WebPush.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// per supported provider: the raw upsert/insert statements must behave the same on each.
/// </summary>
public abstract class WebPushStoreTestBase<TContext>(IWebPushStoreFixture<TContext> fixture) where TContext : DbContext {
    protected static CancellationToken TestToken => TestContext.Current.CancellationToken;

    protected IWebPushStoreFixture<TContext> Fixture => fixture;

    [Fact]
    public async Task SubscriptionStore_UpsertListRemove_Roundtrips() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IPushSubscriptionStore>();
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
        var store = provider.GetRequiredService<IPushSubscriptionStore>();
        var user = NewId();
        using var subscriber = new TestPushSubscriber(NewEndpoint());

        await store.UpsertAsync(subscriber.ToSubscription(user) with { UserAgent = "" }, TestToken);

        (await store.ListByUsersAsync([user], TestToken)).Single().UserAgent.Should().BeNull();
    }

    [Fact]
    public async Task SubscriptionStore_ResubscribeUnderAnotherUser_ReassignsTheOneRow() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IPushSubscriptionStore>();
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
        var store = provider.GetRequiredService<IPushSubscriptionStore>();
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

    protected ServiceProvider CreateProvider() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TContext>(fixture.Configure);
        services.AddElarionWebPushEntityFrameworkCore<TContext>(options => options.Subject = "mailto:ops@example.com");
        return services.BuildServiceProvider();
    }

    protected static string NewId() {
        return Guid.CreateVersion7().ToString("N");
    }

    protected static string NewEndpoint() {
        return $"https://fcm.googleapis.com/fcm/send/{NewId()}";
    }
}
