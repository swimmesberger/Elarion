namespace Elarion.ClientEvents;

/// <summary>
/// One requested subscription as a transport delivered it: a topic, optionally narrowed to a resource key.
/// A topic-only request observes the topic's global scope plus the caller's own user scope; a resource
/// request observes exactly that resource scope (and must pass the
/// <c>IClientEventSubscriptionAuthorizer</c>). Transport-neutral — the SSE endpoint parses its
/// <c>subscriptions</c> query parameter into these, a connection adapter parses its subscribe frame.
/// </summary>
public sealed record ClientEventSubscriptionRequest {
    /// <summary>The topic name (e.g. <c>"invoicing.invoiceChanged"</c>).</summary>
    public required string Topic { get; init; }

    /// <summary>The application-defined resource key (e.g. <c>"customer:42"</c>), when subscribing a
    /// resource scope.</summary>
    public string? Resource { get; init; }
}
