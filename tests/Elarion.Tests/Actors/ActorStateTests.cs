using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions.Serialization;
using Elarion.Actors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Actors;

/// <summary>
/// The ADR-0047 snapshot-state runtime contract: load before the first turn, explicit writes,
/// fail-loud concurrency, reload after passivation, and a pointed failure when no store exists.
/// </summary>
public sealed class ActorStateTests {
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoSnapshot_StartsEmpty_AndWriteCreatesIt() {
        var store = new FakeSnapshotStore();
        await using var provider = CreateProvider(store);
        var vault = provider.GetRequiredService<IActorSystem>().Get<IVault>("a");

        (await vault.Describe(TestToken)).Should().Be("empty");

        (await vault.Deposit(5, TestToken)).Should().Be(5);
        var stored = store.Get(new ActorSnapshotKey("Vault", "a"));
        stored.Should().NotBeNull();
        stored!.Value.Payload.Should().Contain("5");
        // The create minted this lineage's starting version; subsequent writes increment it.
        var lineageVersion = stored.Value.Version;
        lineageVersion.Should().BePositive();

        (await vault.Deposit(3, TestToken)).Should().Be(8);
        store.Get(new ActorSnapshotKey("Vault", "a"))!.Value.Version.Should().Be(lineageVersion + 1);
    }

    [Fact]
    public async Task ExistingSnapshot_IsLoadedBeforeOnActivateAndFirstMessage() {
        var store = new FakeSnapshotStore();
        store.Seed(new ActorSnapshotKey("Vault", "a"), """{"balance":42}""");
        var observed = new ActivationObservations();
        await using var provider = CreateProvider(store, observed);
        var vault = provider.GetRequiredService<IActorSystem>().Get<IVault>("a");

        (await vault.Deposit(1, TestToken)).Should().Be(43);
        // OnActivateAsync already saw the loaded snapshot, not an empty state.
        observed.BalancesOnActivate.Should().Equal(42);
    }

    [Fact]
    public async Task Passivation_DropsMemory_NextActivationReloadsSnapshot() {
        var store = new FakeSnapshotStore();
        var observed = new ActivationObservations();
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(store, observed, time);
        var vault = provider.GetRequiredService<IActorSystem>().Get<IVault>("a");

        await vault.Deposit(7, TestToken);
        await AdvanceUntilAsync(time, TimeSpan.FromMinutes(6), () => observed.Deactivations > 0);

        (await vault.Deposit(1, TestToken)).Should().Be(8);
        observed.BalancesOnActivate.Should().Equal(0, 7);
    }

    [Fact]
    public async Task SnapshotConflict_RetriesTheTurnOnAFreshActivation_CallerNeverSeesIt() {
        var store = new FakeSnapshotStore();
        var observed = new ActivationObservations();
        await using var provider = CreateProvider(store, observed);
        var vault = provider.GetRequiredService<IActorSystem>().Get<IVault>("a");

        await vault.Deposit(5, TestToken);
        // Someone replaced the snapshot underneath this activation (the double-host scenario).
        store.Replace(new ActorSnapshotKey("Vault", "a"), """{"balance":50}""");

        // The conflicted turn is not surfaced: the stale activation passivates, the item re-enters
        // the mailbox, and the retry runs on a fresh activation that loaded the winning snapshot.
        (await vault.Deposit(1, TestToken)).Should().Be(51);

        observed.Deactivations.Should().BeGreaterThanOrEqualTo(1);
        observed.BalancesOnActivate.Should().Equal(0, 50);
        store.Get(new ActorSnapshotKey("Vault", "a"))!.Value.Payload.Should().Contain("51");

        // The replacement activation keeps serving subsequent calls normally.
        (await vault.Deposit(1, TestToken)).Should().Be(52);
    }

    [Fact]
    public async Task SustainedSnapshotConflict_FailsTheCallerAfterExactlyOneRetry() {
        var store = new FakeSnapshotStore();
        await using var provider = CreateProvider(store);
        var vault = provider.GetRequiredService<IActorSystem>().Get<IVault>("a");

        await vault.Deposit(5, TestToken);
        var attemptsBefore = store.WriteAttempts;
        // Every write now conflicts (live contention / sustained double-hosting): the transparent
        // retry is spent on the fresh activation, then the caller sees the conflict.
        store.FailWritesWithConflict = true;

        var act = async () => await vault.Deposit(1, TestToken);
        (await act.Should().ThrowAsync<ActorSnapshotConcurrencyException>())
            .Which.Key.Should().Be(new ActorSnapshotKey("Vault", "a"));
        (store.WriteAttempts - attemptsBefore).Should().Be(2);

        // The store recovers: the next call activates fresh and succeeds.
        store.FailWritesWithConflict = false;
        (await vault.Deposit(1, TestToken)).Should().Be(6);
    }

    [Fact]
    public async Task ForeignSnapshotConflict_FromANestedActorCall_FaultsTheOuterTurn_WithoutRetry() {
        var store = new FakeSnapshotStore();
        await using var provider = CreateProvider(store);
        var turns = provider.GetRequiredService<OrchestratorTurns>();
        var orchestrator = provider.GetRequiredService<IActorSystem>().Get<IOrchestrator>("o");

        // Every vault write conflicts: the vault spends its own transparent retry, then the
        // conflict propagates into the orchestrator's turn as the vault call's failure.
        store.FailWritesWithConflict = true;

        var act = async () => await orchestrator.DepositViaVault(1, TestToken);
        await act.Should().ThrowAsync<ActorSnapshotConcurrencyException>();

        // The conflict's provenance is the vault's activation, not the orchestrator's, so the
        // outer turn ran exactly once (retrying it would double-apply its side effects) and only
        // the vault's own two attempts hit the store.
        turns.Count.Should().Be(1);
        store.WriteAttempts.Should().Be(2);

        // The orchestrator activation was not poisoned: it keeps serving once the store recovers.
        store.FailWritesWithConflict = false;
        (await orchestrator.DepositViaVault(2, TestToken)).Should().Be(2);
        turns.Count.Should().Be(2);
    }

    [Fact]
    public async Task Clear_DeletesTheSnapshotAndResetsState() {
        var store = new FakeSnapshotStore();
        await using var provider = CreateProvider(store);
        var vault = provider.GetRequiredService<IActorSystem>().Get<IVault>("a");

        await vault.Deposit(5, TestToken);
        await vault.Reset(TestToken);

        store.Get(new ActorSnapshotKey("Vault", "a")).Should().BeNull();
        (await vault.Describe(TestToken)).Should().Be("empty");

        // A write after clear creates a fresh snapshot under a freshly minted lineage version.
        await vault.Deposit(2, TestToken);
        store.Get(new ActorSnapshotKey("Vault", "a"))!.Value.Version.Should().BePositive();
    }

    [Fact]
    public async Task StaleETagFromAClearedLineage_NeverMatchesTheRecreatedSnapshot() {
        // The ABA regression: activation A observes an ETag, then someone clears and re-creates the
        // snapshot. A constant create version (the old "always 1") could hand the new lineage the
        // very version A still holds, letting A's guarded write silently overwrite a snapshot it
        // never saw. Lineage-unique minting makes that write fail loudly instead.
        var store = new FakeSnapshotStore();
        var key = new ActorSnapshotKey("Vault", "a");
        var staleETag = await store.WriteAsync(key, """{"balance":1}""", expectedETag: null, TestToken);

        // A second party swaps the whole lineage: clear, then re-create with different state.
        await store.ClearAsync(key, staleETag, TestToken);
        var newETag = await store.WriteAsync(key, """{"balance":100}""", expectedETag: null, TestToken);
        newETag.Should().NotBe(staleETag);

        var act = async () => await store.WriteAsync(key, """{"balance":2}""", staleETag, TestToken);
        await act.Should().ThrowAsync<ActorSnapshotConcurrencyException>();

        // The new lineage's snapshot is untouched by the stale writer.
        (await store.ReadAsync(key, TestToken))!.Payload.Should().Contain("100");
    }

    [Fact]
    public async Task ReadStateAsync_RefreshesFromTheStore() {
        var store = new FakeSnapshotStore();
        await using var provider = CreateProvider(store);
        var vault = provider.GetRequiredService<IActorSystem>().Get<IVault>("a");
        await vault.Deposit(5, TestToken);

        // Someone replaced the snapshot out of band; a deliberate refresh adopts payload AND etag.
        store.Replace(new ActorSnapshotKey("Vault", "a"), """{"balance":100}""");

        (await vault.Reload(TestToken)).Should().Be(100);
        (await vault.Deposit(1, TestToken)).Should().Be(101); // write succeeds against the new etag

        // A refresh after the snapshot disappeared resets to the not-exists state.
        await vault.Reset(TestToken);
        (await vault.Reload(TestToken)).Should().Be(0);
        (await vault.Describe(TestToken)).Should().Be("empty");
    }

    [Fact]
    public async Task WriteWithoutState_Throws() {
        var store = new FakeSnapshotStore();
        await using var provider = CreateProvider(store);
        var vault = provider.GetRequiredService<IActorSystem>().Get<IVault>("a");

        var act = async () => await vault.WriteEmpty(TestToken);
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("assign State");
    }

    [Fact]
    public async Task MissingSnapshotStore_FailsActivationWithPointedMessage() {
        var services = new ServiceCollection();
        services.AddSingleton(new ActivationObservations());
        services.AddElarionJson();
        services.ConfigureElarionJson(options => options.TypeInfoResolvers.Add(ActorStateTestContext.Default));
        services.AddElarionActorSystem();
        AddVaultActor(services);
        await using var provider = services.BuildServiceProvider();
        var vault = provider.GetRequiredService<IActorSystem>().Get<IVault>("a");

        var act = async () => await vault.Deposit(1, TestToken);
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("IActorSnapshotStore");
    }

    private static ServiceProvider CreateProvider(
        FakeSnapshotStore store,
        ActivationObservations? observations = null,
        FakeTimeProvider? timeProvider = null) {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(timeProvider ?? TimeProvider.System);
        services.AddSingleton(observations ?? new ActivationObservations());
        services.AddSingleton<IActorSnapshotStore>(store);
        services.AddSingleton<OrchestratorTurns>();
        services.AddElarionJson();
        services.ConfigureElarionJson(options => options.TypeInfoResolvers.Add(ActorStateTestContext.Default));
        services.AddElarionActorSystem();
        AddVaultActor(services);
        services.AddElarionActor(new ActorRegistration<OrchestratorActor, string, IOrchestrator> {
            Name = "Orchestrator",
            Options = new ActorOptions(),
            Activator = static (serviceProvider, _) => new OrchestratorActor(
                serviceProvider.GetRequiredService<IActorSystem>(),
                serviceProvider.GetRequiredService<OrchestratorTurns>()),
            Facade = static handle => new OrchestratorFacade(handle)
        });
        return services.BuildServiceProvider();
    }

    // Mirrors the generated activator: the state parameter is created through ActorStateFactory,
    // bound to this activation's identity.
    private static void AddVaultActor(IServiceCollection services) =>
        services.AddElarionActor(new ActorRegistration<VaultActor, string, IVault> {
            Name = "Vault",
            Options = new ActorOptions(),
            Activator = static (serviceProvider, context) => new VaultActor(
                ActorStateFactory.Create<VaultState, string>(serviceProvider, context),
                serviceProvider.GetRequiredService<ActivationObservations>()),
            Facade = static handle => new VaultFacade(handle)
        });

    private static async Task AdvanceUntilAsync(FakeTimeProvider time, TimeSpan step, Func<bool> condition) {
        var stopwatch = Stopwatch.StartNew();
        while (!condition()) {
            if (stopwatch.Elapsed > WaitTimeout) {
                throw new TimeoutException("The expected actor state was not reached in time.");
            }

            time.Advance(step);
            await Task.Delay(10, TestToken);
        }
    }

    public sealed record VaultState {
        [JsonPropertyName("balance")]
        public required int Balance { get; init; }
    }

    public sealed class ActivationObservations {
        private readonly ConcurrentQueue<int> _balancesOnActivate = new();
        private int _deactivations;

        public IReadOnlyList<int> BalancesOnActivate => [.. _balancesOnActivate];
        public int Deactivations => Volatile.Read(ref _deactivations);

        public void RecordActivation(int balance) => _balancesOnActivate.Enqueue(balance);
        public void RecordDeactivation() => Interlocked.Increment(ref _deactivations);
    }

    public interface IVault : IActorFacade<string> {
        ValueTask<int> Deposit(int amount, CancellationToken cancellationToken = default);
        ValueTask<string> Describe(CancellationToken cancellationToken = default);
        ValueTask<int> Reload(CancellationToken cancellationToken = default);
        ValueTask Reset(CancellationToken cancellationToken = default);
        ValueTask WriteEmpty(CancellationToken cancellationToken = default);
    }

    public sealed class VaultActor(IActorState<VaultState> state, ActivationObservations observations)
        : IActorLifecycle {
        public ValueTask OnActivateAsync(CancellationToken cancellationToken) {
            observations.RecordActivation(state.State?.Balance ?? 0);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnDeactivateAsync(CancellationToken cancellationToken) {
            observations.RecordDeactivation();
            return ValueTask.CompletedTask;
        }

        public async Task<int> Deposit(int amount, CancellationToken cancellationToken) {
            state.State = new VaultState { Balance = (state.State?.Balance ?? 0) + amount };
            await state.WriteStateAsync(cancellationToken);
            return state.State.Balance;
        }

        public Task<string> Describe(CancellationToken cancellationToken) =>
            Task.FromResult(state.RecordExists
                ? state.State!.Balance.ToString(CultureInfo.InvariantCulture)
                : "empty");

        public async Task<int> Reload(CancellationToken cancellationToken) {
            await state.ReadStateAsync(cancellationToken);
            return state.State?.Balance ?? 0;
        }

        public async Task Reset(CancellationToken cancellationToken) =>
            await state.ClearStateAsync(cancellationToken);

        public async Task WriteEmpty(CancellationToken cancellationToken) =>
            await state.WriteStateAsync(cancellationToken);
    }

    private sealed class VaultFacade(ActorHandle<VaultActor> handle) : IVault {
        public ValueTask<int> Deposit(int amount, CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new DepositItem(amount), cancellationToken);

        public ValueTask<string> Describe(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new DescribeItem(), cancellationToken);

        public ValueTask<int> Reload(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new ReloadItem(), cancellationToken);

        public async ValueTask Reset(CancellationToken cancellationToken = default) =>
            await handle.InvokeAsync(new ResetItem(), cancellationToken);

        public async ValueTask WriteEmpty(CancellationToken cancellationToken = default) =>
            await handle.InvokeAsync(new WriteEmptyItem(), cancellationToken);

        private sealed class DepositItem(int amount) : ActorWorkItem<VaultActor, int> {
            public override string MethodName => "Deposit";

            protected override async ValueTask<int> InvokeAsync(VaultActor actor, CancellationToken cancellationToken) =>
                await actor.Deposit(amount, cancellationToken).ConfigureAwait(false);
        }

        private sealed class DescribeItem : ActorWorkItem<VaultActor, string> {
            public override string MethodName => "Describe";

            protected override async ValueTask<string> InvokeAsync(VaultActor actor, CancellationToken cancellationToken) =>
                await actor.Describe(cancellationToken).ConfigureAwait(false);
        }

        private sealed class ReloadItem : ActorWorkItem<VaultActor, int> {
            public override string MethodName => "Reload";

            protected override async ValueTask<int> InvokeAsync(VaultActor actor, CancellationToken cancellationToken) =>
                await actor.Reload(cancellationToken).ConfigureAwait(false);
        }

        private sealed class ResetItem : ActorWorkItem<VaultActor, bool> {
            public override string MethodName => "Reset";

            protected override async ValueTask<bool> InvokeAsync(VaultActor actor, CancellationToken cancellationToken) {
                await actor.Reset(cancellationToken).ConfigureAwait(false);
                return true;
            }
        }

        private sealed class WriteEmptyItem : ActorWorkItem<VaultActor, bool> {
            public override string MethodName => "WriteEmpty";

            protected override async ValueTask<bool> InvokeAsync(VaultActor actor, CancellationToken cancellationToken) {
                await actor.WriteEmpty(cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
    }

    // --- Orchestrator (a stateless actor whose turn calls the vault — nested-conflict probe) ---

    public sealed class OrchestratorTurns {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Record() => Interlocked.Increment(ref _count);
    }

    public interface IOrchestrator : IActorFacade<string> {
        ValueTask<int> DepositViaVault(int amount, CancellationToken cancellationToken = default);
    }

    public sealed class OrchestratorActor(IActorSystem actors, OrchestratorTurns turns) {
        public async Task<int> DepositViaVault(int amount, CancellationToken cancellationToken) {
            turns.Record();
            return await actors.Get<IVault>("a").Deposit(amount, cancellationToken);
        }
    }

    private sealed class OrchestratorFacade(ActorHandle<OrchestratorActor> handle) : IOrchestrator {
        public ValueTask<int> DepositViaVault(int amount, CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new DepositViaVaultItem(amount), cancellationToken);

        private sealed class DepositViaVaultItem(int amount) : ActorWorkItem<OrchestratorActor, int> {
            public override string MethodName => "DepositViaVault";

            protected override async ValueTask<int> InvokeAsync(OrchestratorActor actor, CancellationToken cancellationToken) =>
                await actor.DepositViaVault(amount, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An in-memory store with the seam's exact ETag semantics: versions as invariant text, minted
    /// lineage-unique at create (never a constant) so a tag from a cleared lineage can never match
    /// a re-created snapshot — the ABA guard the contract requires of every provider.
    /// </summary>
    internal sealed class FakeSnapshotStore : IActorSnapshotStore {
        private readonly ConcurrentDictionary<ActorSnapshotKey, (string Payload, long Version)> _rows = new();
        private int _writeAttempts;

        public int WriteAttempts => Volatile.Read(ref _writeAttempts);

        public bool FailWritesWithConflict { get; set; }

        public (string Payload, long Version)? Get(ActorSnapshotKey key) =>
            _rows.TryGetValue(key, out var row) ? row : null;

        public void Seed(ActorSnapshotKey key, string payload) => _rows[key] = (payload, MintLineageVersion());

        private static long MintLineageVersion() => Random.Shared.NextInt64(1, long.MaxValue >> 1);

        public void BumpVersion(ActorSnapshotKey key) {
            var row = _rows[key];
            _rows[key] = row with { Version = row.Version + 1 };
        }

        public void Replace(ActorSnapshotKey key, string payload) {
            var row = _rows[key];
            _rows[key] = (payload, row.Version + 1);
        }

        public ValueTask<ActorSnapshot?> ReadAsync(ActorSnapshotKey key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ActorSnapshot?>(_rows.TryGetValue(key, out var row)
                ? new ActorSnapshot {
                    Payload = row.Payload,
                    ETag = row.Version.ToString(CultureInfo.InvariantCulture)
                }
                : null);

        public ValueTask<string> WriteAsync(
            ActorSnapshotKey key, string payload, string? expectedETag, CancellationToken cancellationToken = default) {
            Interlocked.Increment(ref _writeAttempts);
            if (FailWritesWithConflict) {
                throw new ActorSnapshotConcurrencyException(key, expectedETag);
            }

            if (expectedETag is null) {
                var version = MintLineageVersion();
                if (!_rows.TryAdd(key, (payload, version))) {
                    throw new ActorSnapshotConcurrencyException(key, expectedETag: null);
                }

                return ValueTask.FromResult(version.ToString(CultureInfo.InvariantCulture));
            }

            var expectedVersion = long.Parse(expectedETag, CultureInfo.InvariantCulture);
            if (!_rows.TryGetValue(key, out var row) || row.Version != expectedVersion ||
                !_rows.TryUpdate(key, (payload, expectedVersion + 1), row)) {
                throw new ActorSnapshotConcurrencyException(key, expectedETag);
            }

            return ValueTask.FromResult((expectedVersion + 1).ToString(CultureInfo.InvariantCulture));
        }

        public ValueTask ClearAsync(ActorSnapshotKey key, string expectedETag, CancellationToken cancellationToken = default) {
            var expectedVersion = long.Parse(expectedETag, CultureInfo.InvariantCulture);
            if (!_rows.TryGetValue(key, out var row) || row.Version != expectedVersion ||
                !_rows.TryRemove(new KeyValuePair<ActorSnapshotKey, (string, long)>(key, row))) {
                throw new ActorSnapshotConcurrencyException(key, expectedETag);
            }

            return ValueTask.CompletedTask;
        }
    }
}

[JsonSerializable(typeof(ActorStateTests.VaultState))]
internal sealed partial class ActorStateTestContext : JsonSerializerContext;
