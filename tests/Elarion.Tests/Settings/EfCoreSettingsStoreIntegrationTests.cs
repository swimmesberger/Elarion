using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Elarion.Settings.InProcess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// Round-trip integration tests for <see cref="EfCoreSettingsStore{TDbContext}"/> against a real PostgreSQL
/// instance. Each test uses unique keys/owners so they stay isolated on the shared database, and skips when
/// Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfCoreSettingsStoreIntegrationTests(PostgreSqlSettingsStoreFixture fixture)
    : IClassFixture<PostgreSqlSettingsStoreFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueKey() => $"app:{Guid.NewGuid():N}";

    private static EfCoreSettingsStore<SettingsIntegrationDbContext> CreateStore(
        SettingsIntegrationDbContext context,
        out InProcessSettingsChangeSource changeSource) {
        changeSource = new InProcessSettingsChangeSource();
        return new EfCoreSettingsStore<SettingsIntegrationDbContext>(context, changeSource, TimeProvider.System,
            NullLogger<EfCoreSettingsStore<SettingsIntegrationDbContext>>.Instance);
    }

    [Fact]
    public async Task SetThenGet_RoundTrips() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();

        var result = await store.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);

        result.IsSuccess.Should().BeTrue();
        result.Version.Should().Be(1);
        (await store.GetAsync(SettingsScope.Global, key, Ct)).Should().Be("v1");
    }

    [Fact]
    public async Task Set_UpdatesValueAndIncrementsVersion() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();

        await store.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);
        var second = await store.SetAsync(SettingsScope.Global, key, "v2", cancellationToken: Ct);

        second.IsSuccess.Should().BeTrue();
        second.Version.Should().Be(2);
        (await store.GetAsync(SettingsScope.Global, key, Ct)).Should().Be("v2");
    }

    [Fact]
    public async Task Set_WithStaleExpectedVersion_ReturnsConflict() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        await store.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, key, "v2", cancellationToken: Ct);

        var result = await store.SetAsync(SettingsScope.Global, key, "v3", expectedVersion: 1, cancellationToken: Ct);

        result.Status.Should().Be(SettingWriteStatus.ConcurrencyConflict);
        (await store.GetAsync(SettingsScope.Global, key, Ct)).Should().Be("v2");
    }

    [Fact]
    public async Task Set_WithMatchingExpectedVersion_Succeeds() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        await store.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);

        var result = await store.SetAsync(SettingsScope.Global, key, "v2", expectedVersion: 1, cancellationToken: Ct);

        result.IsSuccess.Should().BeTrue();
        result.Version.Should().Be(2);
    }

    [Fact]
    public async Task Set_NewKey_WithExpectedExistingVersion_ReturnsConflict() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();

        var result = await store.SetAsync(SettingsScope.Global, key, "v1", expectedVersion: 7, cancellationToken: Ct);

        result.Status.Should().Be(SettingWriteStatus.ConcurrencyConflict);
        (await store.GetAsync(SettingsScope.Global, key, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Scopes_AreIsolated() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        var user = SettingsScope.User(Guid.NewGuid().ToString());

        await store.SetAsync(SettingsScope.Global, key, "global", cancellationToken: Ct);
        await store.SetAsync(user, key, "user", cancellationToken: Ct);

        (await store.GetAsync(SettingsScope.Global, key, Ct)).Should().Be("global");
        (await store.GetAsync(user, key, Ct)).Should().Be("user");
    }

    [Fact]
    public async Task GlobalScope_PersistsWithEmptyOwner() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();

        await store.SetAsync(SettingsScope.Global, key, "v", cancellationToken: Ct);

        await using var verifyContext = fixture.CreateContext();
        var row = await verifyContext.Set<Setting>().AsNoTracking()
            .SingleAsync(setting => setting.Kind == SettingsScope.GlobalKind && setting.Key == key, Ct);
        row.Owner.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_NullValue_PersistsRowWithNullValue() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();

        await store.SetAsync(SettingsScope.Global, key, value: null, cancellationToken: Ct);

        await using var verifyContext = fixture.CreateContext();
        var row = await verifyContext.Set<Setting>().AsNoTracking()
            .SingleAsync(setting => setting.Kind == SettingsScope.GlobalKind && setting.Key == key, Ct);
        row.Value.Should().BeNull();
        row.Version.Should().Be(1);
    }

    [Fact]
    public async Task Remove_DeletesEntry() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        await store.SetAsync(SettingsScope.Global, key, "v", cancellationToken: Ct);

        var removed = await store.RemoveAsync(SettingsScope.Global, key, cancellationToken: Ct);

        removed.Should().BeTrue();
        (await store.GetAsync(SettingsScope.Global, key, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Remove_WithStaleVersion_DoesNotRemove() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        await store.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, key, "v2", cancellationToken: Ct);

        var removed = await store.RemoveAsync(SettingsScope.Global, key, expectedVersion: 1, cancellationToken: Ct);

        removed.Should().BeFalse();
        (await store.GetAsync(SettingsScope.Global, key, Ct)).Should().Be("v2");
    }

    [Fact]
    public async Task GetAll_ReturnsOnlyEntriesInScope() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        // A unique owner gives this test its own isolated key space on the shared database.
        var scope = SettingsScope.User(Guid.NewGuid().ToString());

        await store.SetAsync(scope, "a", "1", cancellationToken: Ct);
        await store.SetAsync(scope, "b", "2", cancellationToken: Ct);

        var all = await store.GetAllAsync(scope, Ct);

        all.Select(entry => entry.Key).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task Set_PublishesChange() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out var changeSource);
        var key = UniqueKey();
        var token = changeSource.Watch(SettingsScope.Global, "app");

        await store.SetAsync(SettingsScope.Global, key, "v", cancellationToken: Ct);

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public async Task Set_Unconditional_UpdatesValueAndIncrementsVersion() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        await store.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);

        var second = await store.SetAsync(SettingsScope.Global, key, "v2", cancellationToken: Ct);

        second.IsSuccess.Should().BeTrue();
        second.Version.Should().Be(2);
        (await store.GetAsync(SettingsScope.Global, key, Ct)).Should().Be("v2");
    }

    [Fact]
    public async Task ConcurrentUnconditionalUpdate_BothSucceed_NoConflict() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var contextA = fixture.CreateContext();
        await using var contextB = fixture.CreateContext();
        var storeA = CreateStore(contextA, out _);
        var storeB = CreateStore(contextB, out _);
        var key = UniqueKey();
        await storeA.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);

        // Both writers observe version 1 and write unconditionally (expectedVersion null); last-write-wins means
        // neither conflicts — the previous behaviour would have conflicted the second on the version guard.
        var first = await storeA.SetAsync(SettingsScope.Global, key, "a", cancellationToken: Ct);
        var second = await storeB.SetAsync(SettingsScope.Global, key, "b", cancellationToken: Ct);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        // The version increments in place, so the second write lands version 3 on top of the first's version 2.
        second.Version.Should().Be(3);
    }

    [Fact]
    public async Task Set_InsideAmbientTransaction_DoesNotPublishChange() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out var changeSource);
        var key = UniqueKey();
        var token = changeSource.Watch(SettingsScope.Global, "app");

        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        await store.SetAsync(SettingsScope.Global, key, "v", cancellationToken: Ct);
        await transaction.CommitAsync(Ct);

        // A transactional write skips the immediate notification so a rollback cannot fire a phantom change.
        token.HasChanged.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentUpdate_LosesOptimisticRace_ReturnsConflict() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var contextA = fixture.CreateContext();
        await using var contextB = fixture.CreateContext();
        var storeA = CreateStore(contextA, out _);
        var storeB = CreateStore(contextB, out _);
        var key = UniqueKey();
        await storeA.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);

        // Both writers expect the same starting version; the first wins, the second must conflict.
        var first = await storeA.SetAsync(SettingsScope.Global, key, "a", expectedVersion: 1, cancellationToken: Ct);
        var second = await storeB.SetAsync(SettingsScope.Global, key, "b", expectedVersion: 1, cancellationToken: Ct);

        first.IsSuccess.Should().BeTrue();
        second.Status.Should().Be(SettingWriteStatus.ConcurrencyConflict);
        (await storeA.GetAsync(SettingsScope.Global, key, Ct)).Should().Be("a");
    }
}
