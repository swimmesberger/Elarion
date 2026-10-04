using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Elarion.Settings.InProcess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// Provider-neutral round-trip contract for <see cref="EfCoreSettingsStore{TDbContext}"/>. The store builds
/// its raw INSERT and <c>UPDATE … RETURNING</c> statements from the EF model, so the same suite runs against
/// every supported relational provider — a derived class per provider binds the matching
/// <see cref="ISettingsStoreFixture"/>. Each test uses unique keys/owners so they stay isolated on the shared
/// database.
/// </summary>
public abstract class EfCoreSettingsStoreTestBase(ISettingsStoreFixture fixture) {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueKey() {
        return $"app:{Guid.NewGuid():N}";
    }

    private static EfCoreSettingsStore<SettingsIntegrationDbContext> CreateStore(
        SettingsIntegrationDbContext context,
        out InProcessSettingsChangeSource changeSource) {
        changeSource = new InProcessSettingsChangeSource();
        var dispatch = new SettingsChangeDispatchScope(changeSource, NullLogger<SettingsChangeDispatchScope>.Instance);
        var notifier = new ChangePublisherSettingsChangeNotifier(dispatch);
        return new EfCoreSettingsStore<SettingsIntegrationDbContext>(context, notifier, TimeProvider.System);
    }

    [Fact]
    public async Task SetThenGet_RoundTrips() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();

        var result = await store.SetAsync(SettingsScope.Global, key, "v1", null, cancellationToken: Ct);

        result.IsSuccess.Should().BeTrue();
        result.Version.Should().Be(1);
        (await store.GetValueAsync(SettingsScope.Global, key, Ct)).Should().Be("v1");
    }

    [Fact]
    public async Task Set_UpdatesValueAndIncrementsVersion() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();

        await store.SetAsync(SettingsScope.Global, key, "v1", null, cancellationToken: Ct);
        var second = await store.SetAsync(SettingsScope.Global, key, "v2", null, cancellationToken: Ct);

        second.IsSuccess.Should().BeTrue();
        second.Version.Should().Be(2);
        (await store.GetValueAsync(SettingsScope.Global, key, Ct)).Should().Be("v2");
    }

    [Fact]
    public async Task Set_WithStaleExpectedVersion_ReturnsConflict() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        await store.SetAsync(SettingsScope.Global, key, "v1", null, cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, key, "v2", null, cancellationToken: Ct);

        var result = await store.SetAsync(SettingsScope.Global, key, "v3", null, 1, Ct);

        result.Status.Should().Be(SettingWriteStatus.ConcurrencyConflict);
        (await store.GetValueAsync(SettingsScope.Global, key, Ct)).Should().Be("v2");
    }

    [Fact]
    public async Task Set_WithMatchingExpectedVersion_Succeeds() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        await store.SetAsync(SettingsScope.Global, key, "v1", null, cancellationToken: Ct);

        var result = await store.SetAsync(SettingsScope.Global, key, "v2", null, 1, Ct);

        result.IsSuccess.Should().BeTrue();
        result.Version.Should().Be(2);
    }

    [Fact]
    public async Task Set_NewKey_WithExpectedExistingVersion_ReturnsConflict() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();

        var result = await store.SetAsync(SettingsScope.Global, key, "v1", null, 7, Ct);

        result.Status.Should().Be(SettingWriteStatus.ConcurrencyConflict);
        (await store.GetValueAsync(SettingsScope.Global, key, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Scopes_AreIsolated() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        var user = SettingsScope.User(Guid.NewGuid().ToString());

        await store.SetAsync(SettingsScope.Global, key, "global", null, cancellationToken: Ct);
        await store.SetAsync(user, key, "user", null, cancellationToken: Ct);

        (await store.GetValueAsync(SettingsScope.Global, key, Ct)).Should().Be("global");
        (await store.GetValueAsync(user, key, Ct)).Should().Be("user");
    }

    [Fact]
    public async Task GlobalScope_PersistsWithEmptyOwner() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();

        await store.SetAsync(SettingsScope.Global, key, "v", null, cancellationToken: Ct);

        await using var verifyContext = fixture.CreateContext();
        var row = await verifyContext.Set<Setting>().AsNoTracking()
            .SingleAsync(setting => setting.Kind == SettingsScope.GlobalKind && setting.Key == key, Ct);
        row.Owner.Should().BeEmpty();
    }

    [Fact]
    public async Task ProtectionMetadata_RoundTripsThroughInsertAndUpdateAndGetAll() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        var owner = SettingsScope.User($"owner-{Guid.NewGuid():N}");

        var created = await store.SetAsync(owner, key, "cipher-1", "scheme.v1", cancellationToken: Ct);
        var createdEntry = await store.GetAsync(owner, key, Ct);
        var updated = await store.SetAsync(owner, key, "cipher-2", "scheme.v2", cancellationToken: Ct);
        var updatedEntry = await store.GetAsync(owner, key, Ct);
        var all = await store.GetAllAsync(owner, Ct);

        created.Version.Should().Be(1);
        createdEntry!.Value.Should().Match<SettingEntry>(e => e.Value == "cipher-1" && e.Protection == "scheme.v1");
        updated.Version.Should().Be(2);
        updatedEntry!.Value.Should().Match<SettingEntry>(e => e.Value == "cipher-2" && e.Protection == "scheme.v2");
        all.Should().ContainSingle().Which.Protection.Should().Be("scheme.v2");
    }

    [Fact]
    public async Task Protection_IsReplacedByAnUnprotectedWrite_AndByTheOptimisticPath() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        await store.SetAsync(SettingsScope.Global, key, "cipher", "scheme.v1", cancellationToken: Ct);

        var guarded = await store.SetAsync(SettingsScope.Global, key, "cipher-b", "scheme.v2", 1, Ct);
        (await store.GetAsync(SettingsScope.Global, key, Ct))!.Value.Protection.Should().Be("scheme.v2");
        await store.SetAsync(SettingsScope.Global, key, "plain", null, cancellationToken: Ct);

        guarded.IsSuccess.Should().BeTrue();
        var entry = await store.GetAsync(SettingsScope.Global, key, Ct);
        entry!.Value.Protection.Should().BeNull();
        entry.Value.Value.Should().Be("plain");
    }

    [Fact]
    public async Task Set_NullValue_PersistsRowWithNullValue() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();

        await store.SetAsync(SettingsScope.Global, key, null, null, cancellationToken: Ct);

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
        await store.SetAsync(SettingsScope.Global, key, "v", null, cancellationToken: Ct);

        var removed = await store.RemoveAsync(SettingsScope.Global, key, cancellationToken: Ct);

        removed.Should().BeTrue();
        (await store.GetValueAsync(SettingsScope.Global, key, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Remove_WithStaleVersion_DoesNotRemove() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        await store.SetAsync(SettingsScope.Global, key, "v1", null, cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, key, "v2", null, cancellationToken: Ct);

        var removed = await store.RemoveAsync(SettingsScope.Global, key, 1, Ct);

        removed.Should().BeFalse();
        (await store.GetValueAsync(SettingsScope.Global, key, Ct)).Should().Be("v2");
    }

    [Fact]
    public async Task GetAll_ReturnsOnlyEntriesInScope() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        // A unique owner gives this test its own isolated key space on the shared database.
        var scope = SettingsScope.User(Guid.NewGuid().ToString());

        await store.SetAsync(scope, "a", "1", null, cancellationToken: Ct);
        await store.SetAsync(scope, "b", "2", null, cancellationToken: Ct);

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

        await store.SetAsync(SettingsScope.Global, key, "v", null, cancellationToken: Ct);

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public async Task Set_Unconditional_UpdatesValueAndIncrementsVersion() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = CreateStore(context, out _);
        var key = UniqueKey();
        await store.SetAsync(SettingsScope.Global, key, "v1", null, cancellationToken: Ct);

        var second = await store.SetAsync(SettingsScope.Global, key, "v2", null, cancellationToken: Ct);

        second.IsSuccess.Should().BeTrue();
        second.Version.Should().Be(2);
        (await store.GetValueAsync(SettingsScope.Global, key, Ct)).Should().Be("v2");
    }

    [Fact]
    public async Task ConcurrentUnconditionalUpdate_BothSucceed_NoConflict() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var contextA = fixture.CreateContext();
        await using var contextB = fixture.CreateContext();
        var storeA = CreateStore(contextA, out _);
        var storeB = CreateStore(contextB, out _);
        var key = UniqueKey();
        await storeA.SetAsync(SettingsScope.Global, key, "v1", null, cancellationToken: Ct);

        // Both writers observe version 1 and write unconditionally (expectedVersion null); last-write-wins means
        // neither conflicts — the previous behaviour would have conflicted the second on the version guard.
        var first = await storeA.SetAsync(SettingsScope.Global, key, "a", null, cancellationToken: Ct);
        var second = await storeB.SetAsync(SettingsScope.Global, key, "b", null, cancellationToken: Ct);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        // The version increments in place, so the second write lands version 3 on top of the first's version 2.
        second.Version.Should().Be(3);
    }

    [Fact]
    public async Task ConcurrentUpdate_LosesOptimisticRace_ReturnsConflict() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var contextA = fixture.CreateContext();
        await using var contextB = fixture.CreateContext();
        var storeA = CreateStore(contextA, out _);
        var storeB = CreateStore(contextB, out _);
        var key = UniqueKey();
        await storeA.SetAsync(SettingsScope.Global, key, "v1", null, cancellationToken: Ct);

        // Both writers expect the same starting version; the first wins, the second must conflict.
        var first = await storeA.SetAsync(SettingsScope.Global, key, "a", null, 1, Ct);
        var second = await storeB.SetAsync(SettingsScope.Global, key, "b", null, 1, Ct);

        first.IsSuccess.Should().BeTrue();
        second.Status.Should().Be(SettingWriteStatus.ConcurrencyConflict);
        (await storeA.GetValueAsync(SettingsScope.Global, key, Ct)).Should().Be("a");
    }
}
