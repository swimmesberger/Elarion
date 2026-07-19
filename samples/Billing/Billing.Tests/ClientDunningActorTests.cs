using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using AwesomeAssertions;
using Billing.Application.Modules.Invoicing;
using Billing.Application.Modules.Invoicing.Actors;
using Billing.Application.Modules.Invoicing.Events;
using Billing.Application.Modules.Invoicing.Handlers;
using Elarion.Abstractions;
using Elarion.Abstractions.Results;
using Elarion.Abstractions.Serialization;
using Elarion.Actors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Billing.Tests;

/// <summary>
/// End-to-end runtime test of the actor event consumer (ADR-0046) and its snapshot state (ADR-0047),
/// Docker-free because the snapshot store seam is swapped for an in-memory fake. It drives the real
/// generated path: the keyed relay is resolved from DI, its <c>HandleAsync</c> resolves the
/// <c>IClientDunning</c> facade by the event's <c>ClientId</c>, the call crosses the mailbox into
/// <see cref="ClientDunningActor"/>, and the generated activator binds <c>IActorState</c> through
/// <c>ActorStateFactory</c>. Registration comes from the module's generated <c>AddInvoicingActors</c>,
/// so this exercises the emitted code, not a hand-wired stand-in.
/// </summary>
public sealed class ClientDunningActorTests {
    private const string RelayKey =
        "global::Billing.Application.Modules.Invoicing.Actors.ClientDunning_OnInvoiceOverdue_EventRelay";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider BuildActorHost(FakeTimeProvider? timeProvider = null) {
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider is not null) services.AddSingleton<TimeProvider>(timeProvider);

        // The snapshot store behind IActorState<ClientDunningState> (ADR-0047). Production registers the
        // PostgreSQL store (AddElarionPostgreSqlActorSnapshots<BillingDbContext>); tests swap the seam for
        // an in-memory fake — actor code and generated wiring are identical either way. The reader is the
        // query-side companion GetClientDunning consumes.
        services.AddSingleton<IActorSnapshotStore>(new InMemorySnapshotStore());
        services.AddSingleton<IActorStateReader, ActorStateReader>();
        services.AddElarionJson();
        services.ConfigureElarionJson(options => options.TypeInfoResolvers.Add(InvoicingJsonContext.Default));

        // The generated per-module registration: wires the actor system, the ClientDunningActor, and its
        // keyed InvoiceOverdue relay (+ the integration-subscription descriptor).
        InvoicingActorExtensions.AddInvoicingActors(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task OverdueEvents_RelayThroughTheKeyedRelay_EscalateThatClient() {
        await using var provider = BuildActorHost();
        var relay = provider.GetRequiredKeyedService<IHandler<InvoiceOverdue, Result<Unit>>>(RelayKey);
        var actors = provider.GetRequiredService<IActorSystem>();

        var clientId = Guid.CreateVersion7();

        // Three invoices for one client fall overdue. Each event routes through the relay into the actor's
        // mailbox and is applied one at a time — the escalation latch trips exactly at the threshold.
        for (var i = 0; i < 3; i++) {
            var result = await relay.HandleAsync(new InvoiceOverdue(Guid.CreateVersion7(), clientId), Ct);
            result.IsSuccess.Should().BeTrue();
        }

        var state = await actors.Get<IClientDunning>(clientId).GetStateAsync(Ct);
        state.OverdueCount.Should().Be(3);
        state.Escalated.Should().BeTrue();
    }

    [Fact]
    public async Task OverdueEvents_AreCoordinatedPerClient() {
        await using var provider = BuildActorHost();
        var relay = provider.GetRequiredKeyedService<IHandler<InvoiceOverdue, Result<Unit>>>(RelayKey);
        var actors = provider.GetRequiredService<IActorSystem>();

        var escalating = Guid.CreateVersion7();
        var quiet = Guid.CreateVersion7();

        for (var i = 0; i < 3; i++) await relay.HandleAsync(new InvoiceOverdue(Guid.CreateVersion7(), escalating), Ct);

        await relay.HandleAsync(new InvoiceOverdue(Guid.CreateVersion7(), quiet), Ct);

        // Each client is an independent activation: one escalates, the other is nowhere near the threshold.
        (await actors.Get<IClientDunning>(escalating).GetStateAsync(Ct)).Escalated.Should().BeTrue();

        var quietState = await actors.Get<IClientDunning>(quiet).GetStateAsync(Ct);
        quietState.OverdueCount.Should().Be(1);
        quietState.Escalated.Should().BeFalse();
    }

    [Fact]
    public async Task DunningQuery_ReadsThePublishedSnapshot_WithoutTouchingTheActor() {
        await using var provider = BuildActorHost();
        var relay = provider.GetRequiredKeyedService<IHandler<InvoiceOverdue, Result<Unit>>>(RelayKey);
        var clientId = Guid.CreateVersion7();

        await relay.HandleAsync(new InvoiceOverdue(Guid.CreateVersion7(), clientId), Ct);
        await relay.HandleAsync(new InvoiceOverdue(Guid.CreateVersion7(), clientId), Ct);

        // The query path (ADR-0047/0048 design rules): GetClientDunning reads the snapshot via
        // IActorStateReader — no IActorSystem, no facade — the shape that runs on any instance,
        // including non-home ones under single-homing. The record carries the interpretation, so the
        // query loses nothing by skipping the actor.
        var handler = new GetClientDunning(provider.GetRequiredService<IActorStateReader>());
        var result = await handler.HandleAsync(new GetClientDunning.Query(clientId), Ct);
        result.IsSuccess.Should().BeTrue();
        result.Value.OverdueCount.Should().Be(2);
        result.Value.NeedsAttention.Should().BeTrue();
        result.Value.Escalated.Should().BeFalse();

        // A client with no dunning history has no snapshot: the query answers Initial, not an error.
        var unknown = await handler.HandleAsync(new GetClientDunning.Query(Guid.CreateVersion7()), Ct);
        unknown.IsSuccess.Should().BeTrue();
        unknown.Value.OverdueCount.Should().Be(0);
    }

    [Fact]
    public void DunningState_IsTheQueryContract_InterpretationAndTransitionsLiveOnTheRecord() {
        // The ADR-0047 design rule: the record owns the threshold, the derived flags, and the pure
        // transition — so an IActorStateReader-based query on a non-home instance shares exactly the
        // logic the actor runs, without running the actor.
        var state = ClientDunningState.Initial;
        state.NeedsAttention.Should().BeFalse();

        state = state.RecordOverdue().RecordOverdue();
        state.NeedsAttention.Should().BeTrue();
        state.Escalated.Should().BeFalse();

        state = state.RecordOverdue();
        state.Escalated.Should().BeTrue();
        state.NeedsAttention.Should().BeFalse();

        // The latch is an event that happened, not a re-derivation: it holds past the threshold.
        state.RecordOverdue().Escalated.Should().BeTrue();
    }

    [Fact]
    public async Task DunningState_SurvivesIdlePassivation() {
        var time = new FakeTimeProvider();
        await using var provider = BuildActorHost(time);
        var relay = provider.GetRequiredKeyedService<IHandler<InvoiceOverdue, Result<Unit>>>(RelayKey);
        var actors = provider.GetRequiredService<IActorSystem>();
        using var telemetry = new ActorDeactivationListener();

        var clientId = Guid.CreateVersion7();
        await relay.HandleAsync(new InvoiceOverdue(Guid.CreateVersion7(), clientId), Ct);
        await relay.HandleAsync(new InvoiceOverdue(Guid.CreateVersion7(), clientId), Ct);

        // Idle past the 5-minute default passivates the activation — in-memory state is dropped. Before
        // ADR-0047 the count reset to zero here; now the next activation reloads the snapshot.
        var stopwatch = Stopwatch.StartNew();
        while (telemetry.Deactivations == 0) {
            if (stopwatch.Elapsed > WaitTimeout)
                throw new TimeoutException("The dunning activation did not passivate in time.");

            time.Advance(TimeSpan.FromMinutes(6));
            await Task.Delay(10, Ct);
        }

        var reloaded = await actors.Get<IClientDunning>(clientId).GetStateAsync(Ct);
        reloaded.OverdueCount.Should().Be(2);

        // The third overdue event still trips the latch exactly at the threshold, across the passivation.
        await relay.HandleAsync(new InvoiceOverdue(Guid.CreateVersion7(), clientId), Ct);
        (await actors.Get<IClientDunning>(clientId).GetStateAsync(Ct)).Escalated.Should().BeTrue();
    }

    /// <summary>Counts actor deactivations via the runtime's public meter (a deactivation is a -1 on the
    /// `actor.activations.active` up-down counter), so the passivation test can wait for the activation to
    /// actually drop instead of sleeping.</summary>
    private sealed class ActorDeactivationListener : IDisposable {
        private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
        private long _deactivations;

        public ActorDeactivationListener() {
            _listener.InstrumentPublished = (instrument, listener) => {
                if (instrument.Meter.Name == "Elarion.Actors" && instrument.Name == "actor.activations.active")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => {
                if (value < 0) Interlocked.Increment(ref _deactivations);
            });
            _listener.Start();
        }

        public long Deactivations => Interlocked.Read(ref _deactivations);

        public void Dispose() {
            _listener.Dispose();
        }
    }

    /// <summary>
    /// An in-memory <see cref="IActorSnapshotStore"/> with the seam's ETag semantics, including
    /// lineage-unique create versions (a tag from a cleared lineage never matches a re-created one).
    /// </summary>
    private sealed class InMemorySnapshotStore : IActorSnapshotStore {
        private readonly ConcurrentDictionary<ActorSnapshotKey, (string Payload, long Version)> _rows = new();

        public ValueTask<ActorSnapshot?>
            ReadAsync(ActorSnapshotKey key, CancellationToken cancellationToken = default) {
            return ValueTask.FromResult<ActorSnapshot?>(_rows.TryGetValue(key, out var row)
                ? new ActorSnapshot {
                    Payload = row.Payload,
                    ETag = row.Version.ToString(CultureInfo.InvariantCulture)
                }
                : null);
        }

        public ValueTask<string> WriteAsync(
            ActorSnapshotKey key, string payload, string? expectedETag, CancellationToken cancellationToken = default) {
            if (expectedETag is null) {
                var version = Random.Shared.NextInt64(1, long.MaxValue >> 1);
                if (!_rows.TryAdd(key, (payload, version))) throw new ActorSnapshotConcurrencyException(key, null);

                return ValueTask.FromResult(version.ToString(CultureInfo.InvariantCulture));
            }

            var expectedVersion = long.Parse(expectedETag, CultureInfo.InvariantCulture);
            if (!_rows.TryGetValue(key, out var row) || row.Version != expectedVersion ||
                !_rows.TryUpdate(key, (payload, expectedVersion + 1), row))
                throw new ActorSnapshotConcurrencyException(key, expectedETag);

            return ValueTask.FromResult((expectedVersion + 1).ToString(CultureInfo.InvariantCulture));
        }

        public ValueTask ClearAsync(ActorSnapshotKey key, string expectedETag,
            CancellationToken cancellationToken = default) {
            var expectedVersion = long.Parse(expectedETag, CultureInfo.InvariantCulture);
            if (!_rows.TryGetValue(key, out var row) || row.Version != expectedVersion ||
                !_rows.TryRemove(new KeyValuePair<ActorSnapshotKey, (string, long)>(key, row)))
                throw new ActorSnapshotConcurrencyException(key, expectedETag);

            return ValueTask.CompletedTask;
        }
    }
}
