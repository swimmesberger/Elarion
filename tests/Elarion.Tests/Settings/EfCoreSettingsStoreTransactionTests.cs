using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Elarion.Settings.InProcess;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// Verifies that <see cref="EfCoreSettingsStore{TDbContext}"/> writes enlist in the caller's ambient EF Core
/// transaction: an insert/update/remove issued on a context that already holds an open transaction commits or
/// rolls back together with that transaction. Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfCoreSettingsStoreTransactionTests(PostgreSqlSettingsStoreFixture fixture)
    : IClassFixture<PostgreSqlSettingsStoreFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueKey() => $"app:{Guid.NewGuid():N}";

    private static EfCoreSettingsStore<SettingsIntegrationDbContext> CreateStore(SettingsIntegrationDbContext context) =>
        new(context, new InProcessSettingsChangeSource(), TimeProvider.System);

    [Fact]
    public async Task SetAsync_Insert_RolledBackWithCallerTransaction_PersistsNothing() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = UniqueKey();

        await using (var context = fixture.CreateContext()) {
            var store = CreateStore(context);
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await store.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);
            await transaction.RollbackAsync(Ct);
        }

        await using var verifyContext = fixture.CreateContext();
        (await CreateStore(verifyContext).GetAsync(SettingsScope.Global, key, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_Insert_CommittedWithCallerTransaction_Persists() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = UniqueKey();

        await using (var context = fixture.CreateContext()) {
            var store = CreateStore(context);
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await store.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);
            await transaction.CommitAsync(Ct);
        }

        await using var verifyContext = fixture.CreateContext();
        (await CreateStore(verifyContext).GetAsync(SettingsScope.Global, key, Ct)).Should().Be("v1");
    }

    [Fact]
    public async Task SetAsync_Update_RolledBackWithCallerTransaction_KeepsPreviousValue() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = UniqueKey();
        await using (var seedContext = fixture.CreateContext()) {
            await CreateStore(seedContext).SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);
        }

        await using (var context = fixture.CreateContext()) {
            var store = CreateStore(context);
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await store.SetAsync(SettingsScope.Global, key, "v2", cancellationToken: Ct);
            await transaction.RollbackAsync(Ct);
        }

        await using var verifyContext = fixture.CreateContext();
        (await CreateStore(verifyContext).GetAsync(SettingsScope.Global, key, Ct)).Should().Be("v1");
    }

    [Fact]
    public async Task RemoveAsync_RolledBackWithCallerTransaction_KeepsEntry() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = UniqueKey();
        await using (var seedContext = fixture.CreateContext()) {
            await CreateStore(seedContext).SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);
        }

        await using (var context = fixture.CreateContext()) {
            var store = CreateStore(context);
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            (await store.RemoveAsync(SettingsScope.Global, key, cancellationToken: Ct)).Should().BeTrue();
            await transaction.RollbackAsync(Ct);
        }

        await using var verifyContext = fixture.CreateContext();
        (await CreateStore(verifyContext).GetAsync(SettingsScope.Global, key, Ct)).Should().Be("v1");
    }
}
