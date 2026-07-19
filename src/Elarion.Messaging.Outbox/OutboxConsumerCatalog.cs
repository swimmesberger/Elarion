using Elarion.Abstractions.Messaging;

namespace Elarion.Messaging.Outbox;

/// <summary>
/// Immutable, process-wide index of durable integration-event consumers by event type and stable consumer id.
/// </summary>
/// <remarks>
/// The catalog validates durable consumer identities once when dependency injection constructs it. Publishing and
/// delivery then perform allocation-free dictionary lookups instead of repeatedly scanning and sorting generated
/// descriptors.
/// </remarks>
public sealed class OutboxConsumerCatalog {
    private static readonly EventSubscriptionDescriptor[] None = [];

    private readonly Dictionary<Type, EventSubscriptionDescriptor[]> _consumersByEventType;
    private readonly Dictionary<string, EventSubscriptionDescriptor[]> _consumersByEventTypeName;
    private readonly HashSet<Type> _roleRoutedEventTypes = [];
    private readonly Dictionary<string, EventSubscriptionDescriptor> _consumersById = new(StringComparer.Ordinal);

    /// <summary>Builds and validates the durable consumer index.</summary>
    public OutboxConsumerCatalog(IEnumerable<EventSubscriptionDescriptor> descriptors) {
        ArgumentNullException.ThrowIfNull(descriptors);

        var byEventType = new Dictionary<Type, List<EventSubscriptionDescriptor>>();
        foreach (var descriptor in descriptors) {
            if (descriptor.Plane is not EventPlane.Integration || descriptor.InvokeAsync is null) continue;

            if (string.IsNullOrWhiteSpace(descriptor.ConsumerId))
                throw new InvalidOperationException(
                    $"Integration-event consumer '{descriptor.ServiceType}' has no stable ConsumerId.");

            if (!_consumersById.TryAdd(descriptor.ConsumerId, descriptor))
                throw new InvalidOperationException(
                    $"Integration-event consumer id '{descriptor.ConsumerId}' is registered more than once.");

            if (!byEventType.TryGetValue(descriptor.EventType, out var consumers)) {
                consumers = [];
                byEventType.Add(descriptor.EventType, consumers);
            }

            consumers.Add(descriptor);
        }

        _consumersByEventType = new Dictionary<Type, EventSubscriptionDescriptor[]>(byEventType.Count);
        _consumersByEventTypeName =
            new Dictionary<string, EventSubscriptionDescriptor[]>(byEventType.Count, StringComparer.Ordinal);
        foreach (var (eventType, consumers) in byEventType) {
            var ordered = consumers.OrderBy(static descriptor => descriptor.Order).ToArray();
            _consumersByEventType.Add(eventType, ordered);
            if (ordered.Any(static descriptor => descriptor.ResolveDeliveryRole is not null))
                _roleRoutedEventTypes.Add(eventType);

            var eventTypeName = eventType.FullName
                                ?? throw new InvalidOperationException(
                                    $"Integration event '{eventType}' has no full name and cannot be persisted.");
            if (!_consumersByEventTypeName.TryAdd(eventTypeName, ordered))
                throw new InvalidOperationException(
                    $"Integration event type name '{eventTypeName}' is registered more than once.");
        }
    }

    /// <summary>Returns the ordered durable consumers for <paramref name="eventType"/>.</summary>
    public IReadOnlyList<EventSubscriptionDescriptor> GetConsumers(Type eventType) {
        ArgumentNullException.ThrowIfNull(eventType);
        return GetConsumerArray(eventType);
    }

    internal EventSubscriptionDescriptor[] GetConsumerArray(Type eventType) {
        return _consumersByEventType.TryGetValue(eventType, out var consumers) ? consumers : None;
    }

    internal bool TryGetConsumerArray(string eventTypeName, out EventSubscriptionDescriptor[] consumers) {
        return _consumersByEventTypeName.TryGetValue(eventTypeName, out consumers!);
    }

    internal bool HasDeliveryRoleResolvers(Type eventType) {
        return _roleRoutedEventTypes.Contains(eventType);
    }

    /// <summary>Looks up one durable consumer by its stable identity.</summary>
    public bool TryGetConsumer(string consumerId, out EventSubscriptionDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(consumerId);
        return _consumersById.TryGetValue(consumerId, out descriptor!);
    }
}
