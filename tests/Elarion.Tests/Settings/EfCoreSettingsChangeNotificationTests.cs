using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// End-to-end verification that the default in-process EF Core settings notifier is commit-gated by the caller's
/// transaction: a write made while an explicit transaction is open fires in-process watch tokens only after the
/// transaction commits, is dropped on rollback, and — across a savepoint — announces only the changes that survived
/// a partial rollback. This exercises the real <see cref="SettingsChangeDispatchTransactionInterceptor"/>
/// auto-attached via <c>AddElarionSettingsEntityFrameworkCore</c> against PostgreSQL (the EF in-memory provider does
/// not raise transaction interceptors). Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfCoreSettingsChangeNotificationTests(PostgreSqlSettingsStoreFixture fixture)
    : IClassFixture<PostgreSqlSettingsStoreFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueKey(string segment) {
        return $"app:{segment}:{Guid.NewGuid():N}";
    }

    [Fact]
    public async Task TransactionalWrite_AnnouncedOnlyAfterCommit() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = BuildProvider();
        var changeSource = provider.GetRequiredService<ISettingsChangeSource>();
        var key = UniqueKey("title");
        var token = changeSource.Watch(SettingsScope.Global, "app");

        await using (var scope = provider.CreateAsyncScope()) {
            var context = scope.ServiceProvider.GetRequiredService<SettingsIntegrationDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await store.SetAsync(SettingsScope.Global, key, "v", cancellationToken: Ct);

            // Still open: the notification is buffered, not yet announced.
            token.HasChanged.Should().BeFalse();
            await transaction.CommitAsync(Ct);
        }

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public async Task TransactionalWrite_DiscardedOnRollback() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = BuildProvider();
        var changeSource = provider.GetRequiredService<ISettingsChangeSource>();
        var key = UniqueKey("title");
        var token = changeSource.Watch(SettingsScope.Global, "app");

        await using (var scope = provider.CreateAsyncScope()) {
            var context = scope.ServiceProvider.GetRequiredService<SettingsIntegrationDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await store.SetAsync(SettingsScope.Global, key, "v", cancellationToken: Ct);
            await transaction.RollbackAsync(Ct);
        }

        token.HasChanged.Should().BeFalse();
    }

    [Fact]
    public async Task NonTransactionalWrite_AnnouncedImmediately() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = BuildProvider();
        var changeSource = provider.GetRequiredService<ISettingsChangeSource>();
        var key = UniqueKey("title");
        var token = changeSource.Watch(SettingsScope.Global, "app");

        await using (var scope = provider.CreateAsyncScope()) {
            var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
            await store.SetAsync(SettingsScope.Global, key, "v", cancellationToken: Ct);
        }

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public async Task RolledBackToSavepoint_AnnouncesOnlyPreSavepointChange() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = BuildProvider();
        var changeSource = provider.GetRequiredService<ISettingsChangeSource>();
        var beforeKey = UniqueKey("before");
        var afterKey = UniqueKey("after");
        var before = changeSource.Watch(SettingsScope.Global, "app:before");
        var after = changeSource.Watch(SettingsScope.Global, "app:after");

        await using (var scope = provider.CreateAsyncScope()) {
            var context = scope.ServiceProvider.GetRequiredService<SettingsIntegrationDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);

            // Written before the savepoint: this change must survive the partial rollback and be announced.
            await store.SetAsync(SettingsScope.Global, beforeKey, "v", cancellationToken: Ct);
            await transaction.CreateSavepointAsync("sp1", Ct);

            // Written after the savepoint: undone by the rollback-to-savepoint and must not be announced even though
            // the outer transaction still commits (the idempotency-decorator shape).
            await store.SetAsync(SettingsScope.Global, afterKey, "v", cancellationToken: Ct);
            await transaction.RollbackToSavepointAsync("sp1", Ct);
            await transaction.CommitAsync(Ct);
        }

        before.HasChanged.Should().BeTrue();
        after.HasChanged.Should().BeFalse();
    }

    private ServiceProvider BuildProvider() {
        var services = new ServiceCollection();
        services.AddLogging();
        // The generic registration auto-attaches the commit-gating interceptor to SettingsIntegrationDbContext via
        // IDbContextOptionsConfiguration, so a plain AddDbContext is all the host needs.
        services.AddElarionSettingsEntityFrameworkCore<SettingsIntegrationDbContext>();
        services.AddDbContext<SettingsIntegrationDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        return services.BuildServiceProvider();
    }
}
