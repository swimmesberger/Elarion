namespace Elarion.ClientEvents;

/// <summary>
/// The composed, immutable set of registered client-event topics: the publisher resolves a contract type to
/// its topic here, and the transport validates + authorizes subscription requests against it. An unregistered
/// topic simply does not exist — subscriptions to it are rejected without revealing anything.
/// </summary>
public sealed class ClientEventTopicCatalog {
    private readonly Dictionary<string, ClientEventTopic> _byName;
    private readonly Dictionary<Type, ClientEventTopic> _byType;

    /// <summary>Composes the catalog, rejecting duplicate topic names and duplicate contract types.</summary>
    /// <param name="topics">The declared topics from every registration call.</param>
    public ClientEventTopicCatalog(IEnumerable<ClientEventTopic> topics) {
        ArgumentNullException.ThrowIfNull(topics);
        _byName = new Dictionary<string, ClientEventTopic>(StringComparer.Ordinal);
        _byType = [];
        foreach (var topic in topics) {
            if (!_byName.TryAdd(topic.Name, topic))
                throw new InvalidOperationException(
                    $"Client-event topic '{topic.Name}' is declared more than once. Topic names must be unique; " +
                    "use the '{module}.{event}' shape to avoid collisions.");
            if (!_byType.TryAdd(topic.EventType, topic))
                throw new InvalidOperationException(
                    $"Client-event type '{topic.EventType}' is declared for more than one topic " +
                    $"('{_byType[topic.EventType].Name}' and '{topic.Name}'). A contract type is the schema of exactly one topic.");
        }
    }

    /// <summary>All registered topics.</summary>
    public IReadOnlyCollection<ClientEventTopic> Topics => _byName.Values;

    /// <summary>The topic named <paramref name="name"/>, or <see langword="null"/> when not registered.</summary>
    public ClientEventTopic? FindByName(string name) {
        return _byName.TryGetValue(name, out var topic) ? topic : null;
    }

    internal ClientEventTopic GetByType(Type eventType) {
        return _byType.TryGetValue(eventType, out var topic)
            ? topic
            : throw new InvalidOperationException(
                $"'{eventType}' is not registered as a client-event topic. Declare it via " +
                "AddElarionClientEvents(events => events.AddTopic<" + eventType.Name + ">(\"{module}.{event}\")) — " +
                "nothing reaches the wire without an explicit topic declaration.");
    }
}
