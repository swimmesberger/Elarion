using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ActorRegistrationGeneratorTests {
    [Fact]
    public void GenerateActors_KeyedActor_EmitsFacadeWorkItemsAndRegistration() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record ShipmentInfo(string Street);

                [Elarion.Actors.Actor]
                public sealed class OrderFulfillmentActor {
                    private int _shipments;

                    public OrderFulfillmentActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    public System.Threading.Tasks.Task<int> Ship(
                        ShipmentInfo info,
                        System.Threading.CancellationToken cancellationToken) {
                        _shipments++;
                        return System.Threading.Tasks.Task.FromResult(_shipments);
                    }

                    public System.Threading.Tasks.ValueTask Reset() {
                        _shipments = 0;
                        return System.Threading.Tasks.ValueTask.CompletedTask;
                    }
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain(
            "public interface IOrderFulfillment : global::Elarion.Actors.IActorFacade<global::System.Guid>");
        generated.Should().Contain(
            "global::System.Threading.Tasks.Task<int> Ship(global::Sample.Orders.ShipmentInfo info, global::System.Threading.CancellationToken cancellationToken = default);");
        generated.Should().Contain("internal sealed class OrderFulfillmentActorFacade : IOrderFulfillment");
        // Facade methods are pass-through (no async wrapper) over a pooled, rented work item: Task
        // shapes bridge via AsTask(), ValueTask shapes return the handle's ValueTask directly
        // (ADR-0042 call-path roadmap).
        generated.Should().Contain("_handle.InvokeAsync(ShipWorkItem.Rent(info), cancellationToken).AsTask();");
        generated.Should().Contain("_handle.InvokeAsync(ResetWorkItem.Rent(), cancellationToken);");
        generated.Should().NotContain("public async");
        generated.Should().Contain(
            "private sealed class ShipWorkItem : global::Elarion.Actors.ActorWorkItem<global::Sample.Orders.OrderFulfillmentActor, int>");
        // Pooled work item: a static Rent over the runtime pool, arguments cleared on Recycle.
        generated.Should().Contain(
            "public static ShipWorkItem Rent(global::Sample.Orders.ShipmentInfo info)");
        generated.Should().Contain(
            "global::Elarion.Actors.Runtime.ActorWorkItemPool<ShipWorkItem>.Rent(static () => new ShipWorkItem());");
        generated.Should().Contain("protected override void Recycle()");
        generated.Should().Contain("global::Elarion.Actors.Runtime.ActorWorkItemPool<ShipWorkItem>.Return(this);");
        generated.Should().Contain("return await actor.Ship(_info, cancellationToken).ConfigureAwait(false);");
        // The void-shaped method rides a Unit work item.
        generated.Should().Contain("global::Elarion.Abstractions.Results.Unit");
        generated.Should().Contain("AddSampleActors");
        generated.Should().Contain(
            "new global::Elarion.Actors.ActorRegistration<global::Sample.Orders.OrderFulfillmentActor, global::System.Guid, global::Sample.Orders.IOrderFulfillment>");
        generated.Should().Contain("Name = \"OrderFulfillment\"");
        generated.Should().Contain("Activator = static (serviceProvider, context) => new global::Sample.Orders.OrderFulfillmentActor(context)");
        generated.Should().Contain("Facade = static handle => new global::Sample.Orders.OrderFulfillmentActorFacade(handle)");
        generated.Should().NotContain("System.Reflection");
    }

    [Fact]
    public void GenerateActors_NullableReferenceResultAndParameters_PreserveTheAnnotation() {
        // Regression: a method returning Task<T?> (or taking a nullable reference parameter) must
        // flow the annotation into the facade and work item — dropping it makes the generated
        // `return await actor.Method(...)` fail CS8603 under warnings-as-errors.
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record Quote(decimal Price);

                [Elarion.Actors.Actor]
                public sealed class QuoteActor {
                    public System.Threading.Tasks.Task<Quote?> Current() =>
                        System.Threading.Tasks.Task.FromResult<Quote?>(null);

                    public System.Threading.Tasks.Task Tag(string? note) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain(
            "global::System.Threading.Tasks.Task<global::Sample.Orders.Quote?> Current(");
        generated.Should().Contain(
            "global::Elarion.Actors.ActorWorkItem<global::Sample.Orders.QuoteActor, global::Sample.Orders.Quote?>");
        generated.Should().Contain("Tag(string? note,");
    }

    [Fact]
    public void GenerateActors_SingleHome_FlowsIntoTheRegistrationOptions() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor(Placement = Elarion.Actors.ActorPlacementMode.SingleHome)]
                public sealed class CoordinatorActor {
                    public System.Threading.Tasks.Task Run(System.Threading.CancellationToken cancellationToken) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }

                [Elarion.Actors.Actor]
                public sealed class RoamingActor {
                    public System.Threading.Tasks.Task Run(System.Threading.CancellationToken cancellationToken) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("Name = \"Coordinator\"");
        generated.Should().Contain("Placement = global::Elarion.Actors.ActorPlacementMode.SingleHome");
        generated.Should().Contain("Placement = global::Elarion.Actors.ActorPlacementMode.Local");
    }

    [Fact]
    public void GenerateActors_VirtualShards_FlowsIntoTheRegistrationOptions() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor(Placement = Elarion.Actors.ActorPlacementMode.VirtualShards)]
                public sealed class ShardedActor {
                    public ShardedActor(Elarion.Actors.IActorContext<string> context) { }

                    public System.Threading.Tasks.Task Run() =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("Placement = global::Elarion.Actors.ActorPlacementMode.VirtualShards");
    }

    [Fact]
    public void GenerateActors_VirtualShardedConsumer_EmitsRoleAffinityResolver() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record OrderChanged(System.Guid OrderId)
                    : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Actors.Actor(Placement = Elarion.Actors.ActorPlacementMode.VirtualShards)]
                public sealed class OrderActor {
                    public OrderActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.Task OnChanged(OrderChanged e) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            assemblyTrigger: """
            [assembly: Elarion.Abstractions.GenerateActors]
            [assembly: Elarion.Abstractions.GenerateEventConsumers]
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        result.Diagnostics.Should().NotContain(d => d.Id == "ELACT014");
        generated.Should().Contain("ResolveDeliveryRole = static (serviceProvider, @event) =>");
        generated.Should().Contain(
            "resolver.Resolve(\"Order\", ((global::Sample.Orders.OrderChanged)@event).OrderId.ToString() ?? string.Empty).Role");
        generated.Should().Contain("ConsumerId = \"global::Sample.Orders.Order_OnChanged_EventRelay\"");
    }

    [Fact]
    public void GenerateActors_StreamMethod_EmitsDeferredFacadeAndSynchronousWorkItem() {
        // ADR-0052: an IAsyncEnumerable<T> actor method becomes a facade stream — the attach turn is
        // deferred until enumeration via ActorStreams.Defer, and the work item invokes synchronously.
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor]
                public sealed class TickerActor {
                    private System.Collections.Generic.IAsyncEnumerable<int> _stream = null!;

                    public TickerActor(Elarion.Actors.IActorContext<string> context) { }

                    public System.Collections.Generic.IAsyncEnumerable<int> Watch(long? after) => _stream;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain(
            "global::System.Collections.Generic.IAsyncEnumerable<int> Watch(long? after, global::System.Threading.CancellationToken cancellationToken = default);");
        generated.Should().Contain("global::Elarion.Actors.Runtime.ActorStreams.Defer<int>(");
        generated.Should().Contain(
            "elarionAttachToken => _handle.InvokeAsync(WatchWorkItem.Rent(after), elarionAttachToken),");
        generated.Should().Contain(
            "private sealed class WatchWorkItem : global::Elarion.Actors.ActorWorkItem<global::Sample.Orders.TickerActor, global::System.Collections.Generic.IAsyncEnumerable<int>>");
        // The stream turn ties the activation's lifetime to the enumeration (refCount lifetime):
        // idle passivation must never end a live stream mid-flight.
        generated.Should().Contain(
            "new(global::Elarion.Actors.Runtime.ActorStreams.RetainWhileEnumerating(actor.Watch(_after), RetainActivation()));");
    }

    [Fact]
    public void GenerateActors_StreamMethodWithCancellationToken_EmitsDiagnostic() {
        // The turn token is a pooled CTS whose lifetime ends with the attach turn — it must never leak
        // into the returned stream, so a CancellationToken parameter on a stream method is rejected.
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor]
                public sealed class TickerActor {
                    private System.Collections.Generic.IAsyncEnumerable<int> _stream = null!;

                    public System.Collections.Generic.IAsyncEnumerable<int> Watch(
                        System.Threading.CancellationToken cancellationToken) => _stream;
                }
            }
            """);

        var result = Generate(source, allowedDiagnosticIds: ["ELACT012"]);

        result.Diagnostics.Any(d => d.Id == "ELACT012" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateActors_ConsumeEventOnStreamMethod_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record OrderShipped(System.Guid OrderId) : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Actors.Actor]
                public sealed class TickerActor {
                    private System.Collections.Generic.IAsyncEnumerable<int> _stream = null!;

                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Collections.Generic.IAsyncEnumerable<int> Watch(OrderShipped e) => _stream;
                }
            }
            """);

        var result = Generate(source, allowedDiagnosticIds: ["ELACT012"]);

        result.Diagnostics.Any(d => d.Id == "ELACT012" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateActors_ActorStateParameter_EmitsFactoryBoundActivator() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record FulfillmentState {
                    public required string Stage { get; init; }
                }

                [Elarion.Actors.Actor]
                public sealed class OrderFulfillmentActor {
                    public OrderFulfillmentActor(
                        Elarion.Actors.IActorContext<System.Guid> context,
                        Elarion.Actors.IActorState<FulfillmentState> state) { }

                    public System.Threading.Tasks.Task Ship(System.Threading.CancellationToken cancellationToken) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        // The state parameter is created through ActorStateFactory bound to this activation's
        // identity (ADR-0047), never resolved from DI like an ordinary service.
        generated.Should().Contain(
            "Activator = static (serviceProvider, context) => new global::Sample.Orders.OrderFulfillmentActor(context, "
            + "global::Elarion.Actors.ActorStateFactory.Create<global::Sample.Orders.FulfillmentState, global::System.Guid>(serviceProvider, context))");
        generated.Should().NotContain("GetRequiredService<global::Elarion.Actors.IActorState");
    }

    [Fact]
    public void GenerateActors_SingletonActorState_BindsTheSingletonKey() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record GaugeState {
                    public required int Value { get; init; }
                }

                [Elarion.Actors.Actor]
                public sealed class GaugeActor {
                    public GaugeActor(Elarion.Actors.IActorState<GaugeState> state) { }

                    public System.Threading.Tasks.Task Bump(System.Threading.CancellationToken cancellationToken) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain(
            "Activator = static (serviceProvider, context) => new global::Sample.Orders.GaugeActor("
            + "global::Elarion.Actors.ActorStateFactory.Create<global::Sample.Orders.GaugeState, global::Elarion.Actors.ActorSingletonKey>(serviceProvider, context))");
    }

    [Fact]
    public void GenerateActors_ModuleScoped_EmitsDefaultServicesFiller() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor]
                public sealed class PingActor {
                    public System.Threading.Tasks.Task Ping() => System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("global::Sample.Orders.SampleActorExtensions.AddSampleActors(services);");
        generated.Should().Contain(
            "static partial void AddActors(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
    }

    [Fact]
    public void GenerateActors_SingletonActor_UsesSingletonKeyAndDependencyInjection() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public interface IClock { }

                [Elarion.Actors.Actor(Name = "Wallet")]
                public sealed class WalletActor {
                    public WalletActor(IClock clock) { }

                    public System.Threading.Tasks.Task<decimal> Balance() =>
                        System.Threading.Tasks.Task.FromResult(0m);
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("public interface IWallet : global::Elarion.Actors.IActorFacade");
        generated.Should().NotContain("IActorFacade<global::Sample.Orders");
        generated.Should().Contain(
            "new global::Elarion.Actors.ActorRegistration<global::Sample.Orders.WalletActor, global::Elarion.Actors.ActorSingletonKey, global::Sample.Orders.IWallet>");
        generated.Should().Contain("serviceProvider.GetRequiredService<global::Sample.Orders.IClock>()");
    }

    [Fact]
    public void GenerateActors_AttributeKnobs_MapToOptions() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor(
                    MailboxCapacity = 128,
                    MailboxFullMode = Elarion.Actors.ActorMailboxFullMode.Fail,
                    IdleTimeoutSeconds = -1,
                    CallTimeoutSeconds = 2.5)]
                [Elarion.Actors.Reentrant]
                public sealed class TunedActor {
                    public System.Threading.Tasks.Task Touch() => System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("MailboxCapacity = 128");
        generated.Should().Contain("MailboxFullMode = global::Elarion.Actors.ActorMailboxFullMode.Fail");
        generated.Should().Contain("IdleTimeout = null");
        generated.Should().Contain("CallTimeout = global::System.TimeSpan.FromSeconds(2.5)");
        generated.Should().Contain("Reentrant = true");
    }

    [Fact]
    public void GenerateActors_ActorOutsideModule_EmitsWarning() {
        var source = CreateSource(
            """
            namespace Sample.Modules {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }
            }

            namespace Other.Place {
                [Elarion.Actors.Actor]
                public sealed class StrayActor {
                    public System.Threading.Tasks.Task Ping() => System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            wrapInModule: false);

        var result = Generate(source);

        result.Diagnostics.Any(d => d.Id == "ELACT003" && d.Severity == DiagnosticSeverity.Warning)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateActors_SynchronousMethod_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor]
                public sealed class BrokenActor {
                    public int Count() => 0;
                }
            }
            """);

        var result = Generate(source, allowedDiagnosticIds: ["ELACT002"]);

        result.Diagnostics.Any(d => d.Id == "ELACT002" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateActors_ConflictingKeyDeclarations_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor(KeyType = typeof(string))]
                public sealed class ConflictedActor {
                    public ConflictedActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    public System.Threading.Tasks.Task Ping() => System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELACT004"]);

        result.Diagnostics.Any(d => d.Id == "ELACT004" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateActors_ConsumeEventOnActorMethod_EmitsInboxDedupedRelay() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record OrderShipped(System.Guid OrderId) : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Actors.Actor]
                public sealed class OrderFulfillmentActor {
                    public OrderFulfillmentActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.Task OnShipped(OrderShipped e) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            assemblyTrigger: """
            [assembly: Elarion.Abstractions.GenerateActors]
            [assembly: Elarion.Abstractions.GenerateEventConsumers]
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        result.Diagnostics.Should().NotContain(d =>
            d.Id == "ELACT007" || d.Id == "ELACT008" || d.Id == "ELACT009" || d.Id == "ELACT010"
            || d.Id == "ELACT011" || d.Id == "ELEVT001" || d.Id == "ELEVT005");

        // The relay resolves the facade by the inferred key and calls the method through the public facade.
        generated.Should().Contain("class OrderFulfillment_OnShipped_EventRelay");
        generated.Should().Contain(
            "_actors.GetByKey<global::Sample.Orders.IOrderFulfillment, global::System.Guid>(request.OrderId)");
        generated.Should().Contain("await facade.OnShipped(request).ConfigureAwait(false)");

        // The reused handler-registration emit attaches the Consumer-scoped inbox, and the module wires the
        // integration subscription.
        generated.Should().Contain("global::Elarion.Pipeline.IdempotencyDecorator<global::Sample.Orders.OrderShipped");
        generated.Should().Contain("global::Elarion.Abstractions.Messaging.EventPlane.Integration");
        generated.Should().Contain("OrderFulfillment_OnShipped_EventRelayRegistration.AddOrderFulfillment_OnShipped_EventRelay(services)");
    }

    [Fact]
    public void GenerateActors_ConsumeEventOnSingletonActor_ResolvesWithoutKey() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record OrderShipped(System.Guid OrderId) : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Actors.Actor]
                public sealed class OrderFulfillmentActor {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.Task OnShipped(OrderShipped e, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            assemblyTrigger: """
            [assembly: Elarion.Abstractions.GenerateActors]
            [assembly: Elarion.Abstractions.GenerateEventConsumers]
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("_actors.Get<global::Sample.Orders.IOrderFulfillment>()");
        generated.Should().Contain("await facade.OnShipped(request, cancellationToken).ConfigureAwait(false)");
    }

    [Fact]
    public void GenerateActors_ConsumeEventAmbiguousKey_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record OrderShipped(System.Guid OrderId, System.Guid CustomerId)
                    : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Actors.Actor]
                public sealed class OrderFulfillmentActor {
                    public OrderFulfillmentActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.Task OnShipped(OrderShipped e) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            assemblyTrigger: """
            [assembly: Elarion.Abstractions.GenerateActors]
            [assembly: Elarion.Abstractions.GenerateEventConsumers]
            """);

        var result = Generate(source, allowedDiagnosticIds: ["ELACT008"]);

        result.Diagnostics.Any(d => d.Id == "ELACT008" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateActors_ConsumeEventActorKeyDisambiguates_EmitsRelay() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record OrderShipped(System.Guid OrderId, System.Guid CustomerId)
                    : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Actors.Actor]
                public sealed class OrderFulfillmentActor {
                    public OrderFulfillmentActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    [Elarion.Actors.ActorKey(nameof(OrderShipped.OrderId))]
                    public System.Threading.Tasks.Task OnShipped(OrderShipped e) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            assemblyTrigger: """
            [assembly: Elarion.Abstractions.GenerateActors]
            [assembly: Elarion.Abstractions.GenerateEventConsumers]
            """);

        var result = Generate(source);

        AllGenerated(result).Should().Contain(
            "_actors.GetByKey<global::Sample.Orders.IOrderFulfillment, global::System.Guid>(request.OrderId)");
    }

    [Fact]
    public void GenerateActors_ConsumeEventOnNonPublicMethod_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record OrderShipped(System.Guid OrderId) : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Actors.Actor]
                public sealed class OrderFulfillmentActor {
                    public OrderFulfillmentActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    internal System.Threading.Tasks.Task OnShipped(OrderShipped e) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            assemblyTrigger: """
            [assembly: Elarion.Abstractions.GenerateActors]
            [assembly: Elarion.Abstractions.GenerateEventConsumers]
            """);

        var result = Generate(source, allowedDiagnosticIds: ["ELACT009"]);

        result.Diagnostics.Any(d => d.Id == "ELACT009" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateActors_ConsumeEventDomainEvent_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record OrderShipped(System.Guid OrderId) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Actors.Actor]
                public sealed class OrderFulfillmentActor {
                    public OrderFulfillmentActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.Task OnShipped(OrderShipped e) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            assemblyTrigger: """
            [assembly: Elarion.Abstractions.GenerateActors]
            [assembly: Elarion.Abstractions.GenerateEventConsumers]
            """);

        var result = Generate(source, allowedDiagnosticIds: ["ELACT010"]);

        result.Diagnostics.Any(d => d.Id == "ELACT010" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateActors_TwoActorsConsumeSameEvent_BothRelaysResolveByKey() {
        // Two actors consuming one event no longer collide: each relay is registered and resolved keyed by
        // its own FQN, so both actors receive the event (ADR-0046 — replaces the former ELACT011).
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record OrderShipped(System.Guid OrderId) : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Actors.Actor]
                public sealed class InventoryActor {
                    public InventoryActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.Task OnShipped(OrderShipped e) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }

                [Elarion.Actors.Actor]
                public sealed class ShippingActor {
                    public ShippingActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.Task OnShipped(OrderShipped e) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            assemblyTrigger: """
            [assembly: Elarion.Abstractions.GenerateActors]
            [assembly: Elarion.Abstractions.GenerateEventConsumers]
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        result.Diagnostics.Should().NotContain(d => d.Id == "ELACT011");
        generated.Should().Contain("class Inventory_OnShipped_EventRelay");
        generated.Should().Contain("class Shipping_OnShipped_EventRelay");
        generated.Should().Contain(
            "GetRequiredKeyedService<global::Elarion.Abstractions.IHandler<global::Sample.Orders.OrderShipped, global::Elarion.Abstractions.Result<global::Elarion.Abstractions.Results.Unit>>>(\"global::Sample.Orders.Inventory_OnShipped_EventRelay\")");
        generated.Should().Contain(
            "GetRequiredKeyedService<global::Elarion.Abstractions.IHandler<global::Sample.Orders.OrderShipped, global::Elarion.Abstractions.Result<global::Elarion.Abstractions.Results.Unit>>>(\"global::Sample.Orders.Shipping_OnShipped_EventRelay\")");
    }

    [Fact]
    public void GenerateActors_NoTrigger_EmitsNothing() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor]
                public sealed class PingActor {
                    public System.Threading.Tasks.Task Ping() => System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            assemblyTrigger: "");

        var result = Generate(source);

        AllGenerated(result).Should().NotContain("IPing");
    }

    [Fact]
    public void GenerateActors_IrrelevantEdit_ReusesPipeline() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record OrderShipped(System.Guid OrderId) : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Actors.Actor]
                public sealed class OrderFulfillmentActor {
                    public OrderFulfillmentActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    public System.Threading.Tasks.Task<int> Count() =>
                        System.Threading.Tasks.Task.FromResult(0);

                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.Task OnShipped(OrderShipped e) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ActorRegistrationGenerator(),
            source,
            "Actors",
            "ActorsCombined");
    }

    private static string CreateSource(
        string testSource,
        string assemblyTrigger = "[assembly: Elarion.Abstractions.GenerateActors]",
        bool wrapInModule = true) {
        // Actors register only per module, so wrap the `Sample.Orders` test namespace in a module by
        // default. Tests asserting no-module behavior pass false.
        var moduleDeclaration = wrapInModule && !testSource.Contains("AppModule(")
            ? """
            namespace Sample.Orders {
                [Elarion.Abstractions.Modules.AppModule("Sample")]
                public static class GeneratedTestModule { }
            }
            """
            : "";

        return $"""
        {assemblyTrigger}

        {moduleDeclaration}

        {testSource}
        """;
    }

    private static string AllGenerated(GeneratorDriverRunResult result) =>
        string.Concat(result.GeneratedTrees.Select(tree => tree.GetText().ToString()));

    private static GeneratorDriverRunResult Generate(
        string source,
        bool assertGeneratedOutputCompiles = true,
        string[]? allowedDiagnosticIds = null) {
        var allowedIds = new HashSet<string>(allowedDiagnosticIds ?? []);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "ActorRegistrationGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(
                new ActorRegistrationGenerator(),
                new EventConsumerRegistrationGenerator(),
                new ModuleDefaultServicesGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics);
        var result = driver.GetRunResult();

        generatorDiagnostics.Concat(result.Diagnostics)
            .Where(d => d.Severity == DiagnosticSeverity.Error && !allowedIds.Contains(d.Id))
            .Should().BeEmpty();

        if (assertGeneratedOutputCompiles) {
            outputCompilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Should().BeEmpty();
        }

        return result;
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
