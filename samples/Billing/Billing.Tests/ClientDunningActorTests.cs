using AwesomeAssertions;
using Billing.Application.Modules.Invoicing;
using Billing.Application.Modules.Invoicing.Actors;
using Billing.Application.Modules.Invoicing.Events;
using Elarion.Abstractions;
using Elarion.Abstractions.Results;
using Elarion.Actors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Billing.Tests;

/// <summary>
/// End-to-end runtime test of the actor event consumer (ADR-0046), Docker-free because the dunning actor is
/// pure in-memory. It drives the real generated path: the keyed relay is resolved from DI, its
/// <c>HandleAsync</c> resolves the <c>IClientDunning</c> facade by the event's <c>ClientId</c>, and the call
/// crosses the mailbox into <see cref="ClientDunningActor"/>. Registration comes from the module's generated
/// <c>AddInvoicingActors</c>, so this exercises the emitted code, not a hand-wired stand-in.
/// </summary>
public sealed class ClientDunningActorTests {
    private const string RelayKey =
        "global::Billing.Application.Modules.Invoicing.Actors.ClientDunning_OnInvoiceOverdue_EventRelay";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider BuildActorHost() {
        var services = new ServiceCollection();
        services.AddLogging();
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

        for (var i = 0; i < 3; i++) {
            await relay.HandleAsync(new InvoiceOverdue(Guid.CreateVersion7(), escalating), Ct);
        }

        await relay.HandleAsync(new InvoiceOverdue(Guid.CreateVersion7(), quiet), Ct);

        // Each client is an independent activation: one escalates, the other is nowhere near the threshold.
        (await actors.Get<IClientDunning>(escalating).GetStateAsync(Ct)).Escalated.Should().BeTrue();

        var quietState = await actors.Get<IClientDunning>(quiet).GetStateAsync(Ct);
        quietState.OverdueCount.Should().Be(1);
        quietState.Escalated.Should().BeFalse();
    }
}
