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
public sealed class PostgreSqlWebPushFixture : IAsyncLifetime, IWebPushStoreFixture<WebPushIntegrationDbContext> {
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

    public void Configure(DbContextOptionsBuilder options) {
        options.UseNpgsql(ConnectionString);
    }

    public WebPushIntegrationDbContext CreateContext() {
        var options = new DbContextOptionsBuilder<WebPushIntegrationDbContext>();
        Configure(options);
        return new WebPushIntegrationDbContext(options.Options);
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
    : WebPushStoreTestBase<WebPushIntegrationDbContext>(fixture), IClassFixture<PostgreSqlWebPushFixture> {
    [Fact]
    public async Task SubscriptionStore_SubscribeRacingAnUncommittedInsert_IsGuardedAgainstTheCommittedWinner() {
        Assert.SkipUnless(Fixture.IsAvailable, Fixture.SkipReason);
        await using var provider = CreateProvider();
        var (alice, mallory) = (NewId(), NewId());
        var endpoint = NewEndpoint();
        using var victim = new TestPushSubscriber(endpoint);
        using var attacker = new TestPushSubscriber(endpoint);

        // Alice's insert holds the unique-index entry uncommitted, so Mallory's statement for the same endpoint
        // waits on it and then evaluates the guard against Alice's committed row: there is no window between a
        // check and a write for Mallory to slip through.
        await using var aliceScope = provider.CreateAsyncScope();
        var aliceContext = aliceScope.ServiceProvider.GetRequiredService<WebPushIntegrationDbContext>();
        await using var transaction = await aliceContext.Database.BeginTransactionAsync(TestToken);
        (await aliceScope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>()
            .UpsertAsync(victim.ToSubscription(alice), TestToken)).Should().BeTrue();

        var malloryWrite = Task.Run(async () => {
            await using var scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>()
                .UpsertAsync(attacker.ToSubscription(mallory), TestToken);
        }, TestToken);
        await transaction.CommitAsync(TestToken);

        (await malloryWrite).Should().BeFalse();
        await using var verify = provider.CreateAsyncScope();
        var stored = (await verify.ServiceProvider.GetRequiredService<IPushSubscriptionStore>()
            .ListByUsersAsync([alice, mallory], TestToken)).Single();
        stored.UserId.Should().Be(alice);
        stored.Auth.Should().Be(victim.Auth);
    }
}
