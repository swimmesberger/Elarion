using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.InProcess;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class InProcessSettingsStoreTests {
    private static InProcessSettingsStore CreateStore(out RecordingChangePublisher publisher) {
        publisher = new RecordingChangePublisher();
        return new InProcessSettingsStore(publisher,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-01-02T03:04:05Z")));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SetThenGet_ReturnsStoredValue() {
        var store = CreateStore(out _);

        await store.SetAsync(SettingsScope.Global, "app:title", "Elarion", cancellationToken: Ct);

        var value = await store.GetAsync(SettingsScope.Global, "app:title", Ct);
        value.Should().Be("Elarion");
    }

    [Fact]
    public async Task Get_ReturnsNull_WhenKeyAbsent() {
        var store = CreateStore(out _);

        var value = await store.GetAsync(SettingsScope.Global, "missing", Ct);

        value.Should().BeNull();
    }

    [Fact]
    public async Task Set_AssignsIncrementingVersions() {
        var store = CreateStore(out _);

        var first = await store.SetAsync(SettingsScope.Global, "k", "1", cancellationToken: Ct);
        var second = await store.SetAsync(SettingsScope.Global, "k", "2", cancellationToken: Ct);

        first.IsSuccess.Should().BeTrue();
        first.Version.Should().Be(1);
        second.Version.Should().Be(2);
    }

    [Fact]
    public async Task Set_WithStaleExpectedVersion_ReturnsConflict() {
        var store = CreateStore(out _);
        await store.SetAsync(SettingsScope.Global, "k", "1", cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, "k", "2", cancellationToken: Ct);

        var result = await store.SetAsync(SettingsScope.Global, "k", "3", 1, Ct);

        result.Status.Should().Be(SettingWriteStatus.ConcurrencyConflict);
        (await store.GetAsync(SettingsScope.Global, "k", Ct)).Should().Be("2");
    }

    [Fact]
    public async Task Set_WithMatchingExpectedVersion_Succeeds() {
        var store = CreateStore(out _);
        await store.SetAsync(SettingsScope.Global, "k", "1", cancellationToken: Ct);

        var result = await store.SetAsync(SettingsScope.Global, "k", "2", 1, Ct);

        result.IsSuccess.Should().BeTrue();
        result.Version.Should().Be(2);
    }

    [Fact]
    public async Task Set_NewKey_WithExpectedExistingVersion_ReturnsConflict() {
        var store = CreateStore(out _);

        var result = await store.SetAsync(SettingsScope.Global, "k", "1", 5, Ct);

        result.Status.Should().Be(SettingWriteStatus.ConcurrencyConflict);
        (await store.GetAsync(SettingsScope.Global, "k", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Scopes_AreIsolated() {
        var store = CreateStore(out _);
        var user = SettingsScope.User("u1");

        await store.SetAsync(SettingsScope.Global, "theme", "light", cancellationToken: Ct);
        await store.SetAsync(user, "theme", "dark", cancellationToken: Ct);

        (await store.GetAsync(SettingsScope.Global, "theme", Ct)).Should().Be("light");
        (await store.GetAsync(user, "theme", Ct)).Should().Be("dark");
    }

    [Fact]
    public async Task Remove_DeletesEntry() {
        var store = CreateStore(out _);
        await store.SetAsync(SettingsScope.Global, "k", "v", cancellationToken: Ct);

        var removed = await store.RemoveAsync(SettingsScope.Global, "k", cancellationToken: Ct);

        removed.Should().BeTrue();
        (await store.GetAsync(SettingsScope.Global, "k", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Remove_WithStaleVersion_DoesNotRemove() {
        var store = CreateStore(out _);
        await store.SetAsync(SettingsScope.Global, "k", "1", cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, "k", "2", cancellationToken: Ct);

        var removed = await store.RemoveAsync(SettingsScope.Global, "k", 1, Ct);

        removed.Should().BeFalse();
        (await store.GetAsync(SettingsScope.Global, "k", Ct)).Should().Be("2");
    }

    [Fact]
    public async Task GetAll_ReturnsOnlyEntriesInScope() {
        var store = CreateStore(out _);
        await store.SetAsync(SettingsScope.Global, "a", "1", cancellationToken: Ct);
        await store.SetAsync(SettingsScope.Global, "b", "2", cancellationToken: Ct);
        await store.SetAsync(SettingsScope.User("u1"), "c", "3", cancellationToken: Ct);

        var all = await store.GetAllAsync(SettingsScope.Global, Ct);

        all.Select(entry => entry.Key).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task Set_PublishesChange() {
        var store = CreateStore(out var publisher);

        await store.SetAsync(SettingsScope.Global, "app:title", "Elarion", cancellationToken: Ct);

        publisher.Published.Should().ContainSingle()
            .Which.Should().Be((SettingsScope.Global, "app:title"));
    }

    [Fact]
    public async Task Remove_PublishesChange() {
        var store = CreateStore(out var publisher);
        await store.SetAsync(SettingsScope.Global, "k", "v", cancellationToken: Ct);
        publisher.Published.Clear();

        await store.RemoveAsync(SettingsScope.Global, "k", cancellationToken: Ct);

        publisher.Published.Should().ContainSingle().Which.Key.Should().Be("k");
    }

    [Fact]
    public async Task Set_Conflict_DoesNotPublish() {
        var store = CreateStore(out var publisher);

        await store.SetAsync(SettingsScope.Global, "k", "1", 9, Ct);

        publisher.Published.Should().BeEmpty();
    }

    private sealed class RecordingChangePublisher : ISettingsChangePublisher {
        public List<(SettingsScope Scope, string Key)> Published { get; } = [];

        public void Publish(SettingsScope scope, string key) {
            Published.Add((scope, key));
        }
    }
}
