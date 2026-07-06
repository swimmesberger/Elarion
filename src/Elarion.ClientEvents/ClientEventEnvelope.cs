using Elarion.Abstractions.ClientEvents;

namespace Elarion.ClientEvents;

/// <summary>
/// One published client event, ready for transport: the topic, the audience scope, and the payload already
/// serialized to canonical JSON. Serializing once at publish means every fan-out path (local subscribers, a
/// cross-node broadcaster) forwards the same bytes and no transport needs the contract's type info.
/// </summary>
public sealed record ClientEventEnvelope {
    /// <summary>A time-ordered id for this publish (the SSE <c>id:</c> field).</summary>
    public required Guid Id { get; init; }

    /// <summary>The topic name (the SSE <c>event:</c> field).</summary>
    public required string Topic { get; init; }

    /// <summary>The audience of this publish.</summary>
    public required ClientEventScope Scope { get; init; }

    /// <summary>The payload as canonical JSON text.</summary>
    public required string Payload { get; init; }
}
