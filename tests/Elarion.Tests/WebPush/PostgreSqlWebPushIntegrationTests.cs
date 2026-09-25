using AwesomeAssertions;
using Elarion.WebPush;
using Elarion.WebPush.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.WebPush;

/// <summary>
/// Starts a disposable PostgreSQL container for the Web Push store integration tests and creates the schema
/// once. Skips (never fails) when Docker is unavailable, mirroring the device identity fixture.
/// </summary>
public sealed class PostgreSqlWebPushFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = "";

    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync() {
        PostgreSqlContainer container;
        try {
            container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
        }
        catch (Exception ex) {
            SkipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
            return;
        }

        _container = container;
        ConnectionString = container.GetConnectionString();
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) await _container.DisposeAsync();
    }

    public WebPushIntegrationDbContext CreateContext() {
        return new WebPushIntegrationDbContext(new DbContextOptionsBuilder<WebPushIntegrationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);
    }
}

/// <summary>Context mapping the Web Push tables the way a generated context would (the seam calls <c>UseElarionWebPush</c>).</summary>
public sealed class WebPushIntegrationDbContext(DbContextOptions<WebPushIntegrationDbContext> options)
    : DbContext(options) {
    public DbSet<PushSubscriptionEntity> PushSubscriptions => Set<PushSubscriptionEntity>();

    public DbSet<VapidKeyEntity> VapidKeys => Set<VapidKeyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.UseElarionWebPush();
    }
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlWebPushIntegrationTests(PostgreSqlWebPushFixture fixture)
    : IClassFixture<PostgreSqlWebPushFixture> {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

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
        (await context.PushSubscriptions.CountAsync(entity => entity.Endpoint == subscriber.Endpoint, TestToken))
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
            await context.VapidKeys.ExecuteDeleteAsync(TestToken);
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

    private ServiceProvider CreateProvider() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<WebPushIntegrationDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddElarionWebPushEntityFrameworkCore<WebPushIntegrationDbContext>(options =>
            options.Subject = "mailto:ops@example.com");
        return services.BuildServiceProvider();
    }

    private static string NewId() {
        return Guid.CreateVersion7().ToString("N");
    }

    private static string NewEndpoint() {
        return $"https://fcm.googleapis.com/fcm/send/{NewId()}";
    }
}
