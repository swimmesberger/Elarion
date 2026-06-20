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

/// <summary>
/// Invokes a generated responder delegate without runtime reflection, returning the boxed
/// <see cref="Result{TResponse}"/> produced by the responder.
/// </summary>
public delegate ValueTask<object> EventResponderInvokeDelegate(
    IServiceProvider serviceProvider,
    object request,
    IEventContext context,
    CancellationToken ct);

/// <summary>
/// Immutable source-generated metadata and invocation logic for one event consumer.
/// </summary>
/// <remarks>
/// Application code normally does not construct descriptors directly; they are emitted by the event
/// consumer source generator. Manual descriptors are useful in tests or advanced host wiring. A
/// descriptor is either a fan-out subscriber (<see cref="InvokeAsync"/> set, <see cref="ResponseType"/>
/// null) or a responder (<see cref="InvokeRequestAsync"/> set, <see cref="ResponseType"/> non-null).
/// </remarks>
public sealed record EventSubscriptionDescriptor {
    /// <summary>The message type this consumer handles (the dispatch address).</summary>
    public required Type EventType { get; init; }

    /// <summary>The dispatch plane this consumer participates in.</summary>
    public required EventPlane Plane { get; init; }

    /// <summary>The <c>[Service]</c> type that declares the consumer method.</summary>
    public required Type ServiceType { get; init; }

    /// <summary>
    /// The response payload type for a responder, or <c>null</c> for a fan-out subscriber.
    /// </summary>
    public Type? ResponseType { get; init; }

    /// <summary>Ascending fan-out invocation order; ignored for responders.</summary>
    public int Order { get; init; }

    /// <summary>
    /// The owning module name used for configuration gating, or <c>null</c> when the consumer
    /// belongs to no module (mapped ungated).
    /// </summary>
    public string? Module { get; init; }

    /// <summary>The fan-out subscriber delegate. Set when <see cref="ResponseType"/> is <c>null</c>.</summary>
    public EventSubscriberInvokeDelegate? InvokeAsync { get; init; }

    /// <summary>The responder delegate. Set when <see cref="ResponseType"/> is non-<c>null</c>.</summary>
    public EventResponderInvokeDelegate? InvokeRequestAsync { get; init; }

    /// <summary>True when this descriptor responds to an <see cref="IDomainEventBus.RequestAsync{TRequest,TResponse}"/> call.</summary>
    public bool IsResponder => ResponseType is not null;
}
