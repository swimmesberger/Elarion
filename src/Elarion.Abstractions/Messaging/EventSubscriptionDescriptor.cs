namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Invokes a generated fan-out subscriber delegate without runtime reflection.
/// </summary>
/// <remarks>
/// The source generator emits delegates of this shape so the runtime can invoke consumer methods
/// without reflection or dynamic dispatch. The consumer is resolved from
/// <paramref name="serviceProvider"/>, which is the dispatch scope's provider.
/// </remarks>
public delegate ValueTask EventSubscriberInvokeDelegate(
    IServiceProvider serviceProvider,
    object @event,
    IEventContext context,
    CancellationToken ct);

/// <summary>Resolves the coarse role that must execute one durable consumer delivery.</summary>
/// <remarks>
/// A <see langword="null"/> result means any outbox worker may claim the delivery. The resolver runs
/// while publishing, in the publisher's dependency-injection scope, and must not perform remote I/O.
/// </remarks>
public delegate string? EventDeliveryRoleResolver(IServiceProvider serviceProvider, object @event);

/// <summary>
/// Immutable source-generated metadata and invocation logic for one event consumer (a fan-out subscriber).
/// </summary>
/// <remarks>
/// Application code normally does not construct descriptors directly; they are emitted by the event
/// consumer source generator. Manual descriptors are useful in tests or advanced host wiring. Every
/// consumer is a fan-out subscriber — the event bus is pub/sub-only (see ADR-0010); request/reply is served
/// by typed dispatch (<c>IHandlerSender</c>/<c>IHandler</c>) or the named <c>HandlerDispatcher</c>.
/// </remarks>
public sealed record EventSubscriptionDescriptor {
    /// <summary>
    /// Stable identity of this consumer. Durable outbox fan-out stores it when one publish splits into target groups.
    /// </summary>
    public string ConsumerId { get; init; } = string.Empty;

    /// <summary>The message type this consumer handles (the dispatch address).</summary>
    public required Type EventType { get; init; }

    /// <summary>The dispatch plane this consumer participates in.</summary>
    public required EventPlane Plane { get; init; }

    /// <summary>The <c>[Service]</c> type that declares the consumer method.</summary>
    public required Type ServiceType { get; init; }

    /// <summary>Ascending fan-out invocation order.</summary>
    public int Order { get; init; }

    /// <summary>
    /// The owning module name used for configuration gating, or <c>null</c> when the consumer
    /// belongs to no module (mapped ungated).
    /// </summary>
    public string? Module { get; init; }

    /// <summary>
    /// Optional role-affinity resolver for durable delivery. It selects where this consumer executes;
    /// it does not affect in-memory event dispatch.
    /// </summary>
    public EventDeliveryRoleResolver? ResolveDeliveryRole { get; init; }

    /// <summary>The fan-out subscriber delegate.</summary>
    public EventSubscriberInvokeDelegate? InvokeAsync { get; init; }
}
