using System.Diagnostics;
using Elarion.Abstractions.Messaging;

namespace Elarion.Messaging.InMemory;

/// <summary>
/// One integration event captured for after-commit delivery.
/// </summary>
/// <remarks>
/// The context is built at publish time (when the message type is statically known) so the delivery
/// pump can reuse it without reflecting over the erased message type. <paramref name="TraceParent"/>
/// carries the publisher's trace context across the commit boundary so the after-commit consumer
/// span stays parented to the publishing operation.
/// </remarks>
internal readonly record struct EventEnvelope(
    object Event,
    Type EventType,
    IEventContext Context,
    ActivityContext TraceParent = default);
