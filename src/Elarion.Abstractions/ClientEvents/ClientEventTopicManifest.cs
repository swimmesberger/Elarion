namespace Elarion.Abstractions.ClientEvents;

/// <summary>
/// The export-facing view of the registered client-event topics: what the schema exporter needs to emit the
/// schema's <c>events</c> block (topic names + payload contract types), and nothing else. Registered by
/// <c>AddElarionClientEvents</c> and resolved from the host's DI container by the build-time schema tool —
/// the same pattern as <see cref="Modules.ClientCapabilityManifest"/> (ADR-0032), which is why it lives in
/// Abstractions rather than the client-events package.
/// </summary>
public sealed record ClientEventTopicManifest {
    /// <summary>The declared topics.</summary>
    public required IReadOnlyList<ClientEventTopicManifestEntry> Topics { get; init; }
}

/// <summary>One declared client-event topic: its wire name and the contract type serialized as its payload.</summary>
public sealed record ClientEventTopicManifestEntry {
    /// <summary>The topic name clients subscribe to (e.g. <c>"invoicing.invoiceChanged"</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The <see cref="IClientEvent"/> contract type; the exporter reflects its JSON schema.</summary>
    public required Type EventType { get; init; }
}
