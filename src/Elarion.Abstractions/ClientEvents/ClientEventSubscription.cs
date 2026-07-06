namespace Elarion.Abstractions.ClientEvents;

/// <summary>One subscription a client asked for: a topic within one <see cref="ClientEventScope"/>.</summary>
public sealed record ClientEventSubscription {
    /// <summary>The topic name (e.g. <c>"invoicing.invoiceChanged"</c>).</summary>
    public required string Topic { get; init; }

    /// <summary>The audience scope the client wants to observe.</summary>
    public required ClientEventScope Scope { get; init; }
}
