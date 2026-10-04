using System.Net;
using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.DataProtection;
using Elarion.WebPush;
using Elarion.WebPush.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
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
    public async Task SubscriptionStore_ListSubscribedUserIds_ReturnsDistinctOwnersSorted() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>();
        var (alice, bob, carol) = ($"a-{NewId()}", $"b-{NewId()}", $"c-{NewId()}");
        using var phone = new TestPushSubscriber(NewEndpoint());
        using var laptop = new TestPushSubscriber(NewEndpoint());
        using var tablet = new TestPushSubscriber(NewEndpoint());
        await store.UpsertAsync(phone.ToSubscription(bob), TestToken);
        await store.UpsertAsync(laptop.ToSubscription(bob), TestToken);
        await store.UpsertAsync(tablet.ToSubscription(alice), TestToken);

        (await store.ListSubscribedUserIdsAsync(cancellationToken: TestToken))
            .Should().ContainInConsecutiveOrder(alice, bob).And.OnlyHaveUniqueItems();
        (await store.ListSubscribedUserIdsAsync([carol, bob, "nobody"], TestToken)).Should().Equal(bob);
        (await store.ListSubscribedUserIdsAsync([], TestToken)).Should().BeEmpty();
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

    [Fact]
    public async Task VapidKeyStore_PrivateKey_IsProtectedAtRestAndRoundtrips() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await ClearKeysAsync();
        await using var provider = CreateProvider();

        var keys = await provider.GetRequiredService<IVapidKeyProvider>().GetAsync(TestToken);

        await using var context = fixture.CreateContext();
        var row = await context.Set<VapidKeyEntity>().AsNoTracking().SingleAsync(TestToken);
        row.PublicKey.Should().Be(keys.PublicKey);
        row.PrivateKey.Should().NotBe(keys.PrivateKey).And.NotContain(keys.PrivateKey);
        row.Protection.Should().Be(DataProtectionSettingValueProtector.SchemeId);
        (await provider.GetRequiredService<IVapidKeyStore>().GetAsync(TestToken)).Should().Be(keys);
        await using var otherNode = CreateProvider();
        (await otherNode.GetRequiredService<IVapidKeyStore>().GetAsync(TestToken))!.PrivateKey.Should().Be(keys.PrivateKey);
    }

    [Fact]
    public async Task VapidKeyStore_LegacyPlaintextRow_IsAcceptedAndReprotectedInPlace() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await ClearKeysAsync();
        var legacy = VapidKeys.Generate();
        await using (var context = fixture.CreateContext()) {
            context.Add(new VapidKeyEntity {
                Name = VapidKeyEntity.DefaultName,
                PublicKey = legacy.PublicKey,
                PrivateKey = legacy.PrivateKey,
                CreatedOnUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync(TestToken);
        }

        await using var provider = CreateProvider();
        var resolved = await provider.GetRequiredService<IVapidKeyProvider>().GetAsync(TestToken);

        resolved.Should().Be(legacy);
        await using var verify = fixture.CreateContext();
        var row = await verify.Set<VapidKeyEntity>().AsNoTracking().SingleAsync(TestToken);
        row.Protection.Should().Be(DataProtectionSettingValueProtector.SchemeId);
        row.PrivateKey.Should().NotContain(legacy.PrivateKey);
        (await provider.GetRequiredService<IVapidKeyStore>().GetAsync(TestToken)).Should().Be(legacy);
    }

    [Fact]
    public async Task VapidKeyStore_UnreadablePayload_FailsClosedInsteadOfGeneratingAReplacement() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await ClearKeysAsync();
        await using (var first = CreateProvider())
            await first.GetRequiredService<IVapidKeyProvider>().GetAsync(TestToken);

        // A different key ring cannot read the row; it must not mint a new pair over the stored one.
        await using var foreign = CreateProvider(protector: NewProtector());
        var act = async () => await foreign.GetRequiredService<IVapidKeyProvider>().GetAsync(TestToken);

        await act.Should().ThrowAsync<SettingProtectionException>();
        await using (var context = fixture.CreateContext())
            (await context.Set<VapidKeyEntity>().CountAsync(TestToken)).Should().Be(1);
        await ClearKeysAsync();
    }

    [Fact]
    public async Task VapidKeyStore_WithoutAProtector_FailsClosed() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TContext>(fixture.Configure);
        services.AddElarionWebPushEntityFrameworkCore<TContext>(options => options.Subject = "mailto:ops@example.com");
        await using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IVapidKeyStore>();

        act.Should().Throw<SettingProtectionException>().WithMessage("*ISettingValueProtector*");
    }

    [Fact]
    public async Task VapidKeyStore_ACustomStoreRegisteredAfterwards_NeedsNoProtector() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TContext>(fixture.Configure);
        services.AddElarionWebPushEntityFrameworkCore<TContext>(options => options.Subject = "mailto:ops@example.com");
        services.AddSingleton<IVapidKeyStore, InMemoryVapidKeyStore>();
        await using var provider = services.BuildServiceProvider();

        var keys = await provider.GetRequiredService<IVapidKeyProvider>().GetAsync(TestToken);

        keys.PublicKey.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VapidKeyStore_WorksWithTheShippedDataProtectionRegistration() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await ClearKeysAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionSettingsDataProtection(o => o.ReprotectOnStartup = false);
        services.AddDbContext<TContext>(fixture.Configure);
        services.AddElarionWebPushEntityFrameworkCore<TContext>(options => options.Subject = "mailto:ops@example.com");
        await using var provider = services.BuildServiceProvider();

        try {
            var keys = await provider.GetRequiredService<IVapidKeyProvider>().GetAsync(TestToken);

            (await provider.GetRequiredService<IVapidKeyStore>().GetAsync(TestToken)).Should().Be(keys);
        }
        finally {
            // This host has a key ring of its own: its protected row must not outlive it for the shared-ring tests.
            await ClearKeysAsync();
        }
    }

    [Fact]
    public async Task VapidKeyStore_Import_RoundtripsTheExactPairUnderTheStoresOwnProtection() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await ClearKeysAsync();
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IVapidKeyStore>();
        var known = VapidKeys.Generate();

        (await store.ImportAsync(known, cancellationToken: TestToken)).Should().Be(VapidKeyImportResult.Imported);

        var read = await store.GetAsync(TestToken);
        read!.PublicKey.Should().Be(known.PublicKey);
        read.PrivateKey.Should().Be(known.PrivateKey);
        await using var context = fixture.CreateContext();
        var row = await context.Set<VapidKeyEntity>().AsNoTracking().SingleAsync(TestToken);
        row.PrivateKey.Should().NotContain(known.PrivateKey);
        row.Protection.Should().Be(DataProtectionSettingValueProtector.SchemeId);
        (await provider.GetRequiredService<IVapidKeyProvider>().GetAsync(TestToken)).Should().Be(known);
    }

    [Fact]
    public async Task VapidKeyStore_Import_SamePairIsANoOp_DifferentPairIsRefusedUnlessOverwritten() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await ClearKeysAsync();
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IVapidKeyStore>();
        var known = VapidKeys.Generate();
        var other = VapidKeys.Generate();
        await store.ImportAsync(known, cancellationToken: TestToken);

        (await store.ImportAsync(known, cancellationToken: TestToken)).Should().Be(VapidKeyImportResult.Unchanged);
        (await store.ImportAsync(other, cancellationToken: TestToken)).Should().Be(VapidKeyImportResult.Refused);
        (await store.GetAsync(TestToken)).Should().Be(known);

        (await store.ImportAsync(other, overwrite: true, TestToken)).Should().Be(VapidKeyImportResult.Replaced);
        (await store.GetAsync(TestToken)).Should().Be(other);
        await using var context = fixture.CreateContext();
        (await context.Set<VapidKeyEntity>().CountAsync(TestToken)).Should().Be(1);
    }

    [Fact]
    public async Task VapidKeyStore_Import_ConcurrentNodesLeaveExactlyOneWinner() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await ClearKeysAsync();
        var candidates = Enumerable.Range(0, 6).Select(_ => VapidKeys.Generate()).ToArray();
        var providers = candidates.Select(_ => CreateProvider()).ToArray();
        try {
            var results = await Task.WhenAll(providers.Select((provider, i) =>
                provider.GetRequiredService<IVapidKeyStore>().ImportAsync(candidates[i], cancellationToken: TestToken)
                    .AsTask()));

            results.Count(r => r == VapidKeyImportResult.Imported).Should().Be(1);
            results.Count(r => r == VapidKeyImportResult.Refused).Should().Be(candidates.Length - 1);
            var winner = await providers[0].GetRequiredService<IVapidKeyStore>().GetAsync(TestToken);
            candidates.Should().Contain(winner!);
        }
        finally {
            foreach (var provider in providers) await provider.DisposeAsync();
        }
    }

    private async Task ClearKeysAsync() {
        await using var context = fixture.CreateContext();
        await context.Set<VapidKeyEntity>().ExecuteDeleteAsync(TestToken);
    }

    // One key ring for the whole class, so providers standing in for nodes can read each other's rows.
    private static readonly ISettingValueProtector SharedProtector = NewProtector();

    private static ISettingValueProtector NewProtector() {
        return new DataProtectionSettingValueProtector(new EphemeralDataProtectionProvider());
    }

    private protected ServiceProvider CreateProvider(FakePushService? pushService = null,
        ISettingValueProtector? protector = null) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(protector ?? SharedProtector);
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
