using Elarion.Abstractions.Messaging;

namespace Elarion.Messaging;

/// <summary>
/// Indexes event consumer descriptors by message type and plane for reflection-free lookup.
/// </summary>
/// <remarks>
/// Built once as a singleton from the registered descriptors, then queried by the scoped buses and
/// the delivery pump on every publish.
/// </remarks>
internal sealed class EventSubscriptionRegistry {
    private static readonly EventSubscriptionDescriptor[] None = [];

    private readonly Dictionary<Type, EventSubscriptionDescriptor[]> _domainSubscribers;
    private readonly Dictionary<Type, EventSubscriptionDescriptor[]> _integrationSubscribers;

    public EventSubscriptionRegistry(IEnumerable<EventSubscriptionDescriptor> descriptors) {
        var domainSubscribers = new Dictionary<Type, List<EventSubscriptionDescriptor>>();
        var integrationSubscribers = new Dictionary<Type, List<EventSubscriptionDescriptor>>();

        foreach (var descriptor in descriptors) {
            var target = descriptor.Plane is EventPlane.Domain ? domainSubscribers : integrationSubscribers;
            if (!target.TryGetValue(descriptor.EventType, out var list)) {
                list = [];
                target[descriptor.EventType] = list;
            }

            list.Add(descriptor);
        }

        _domainSubscribers = Freeze(domainSubscribers);
        _integrationSubscribers = Freeze(integrationSubscribers);
    }

    public EventSubscriptionDescriptor[] GetDomainSubscribers(Type eventType) {
        return _domainSubscribers.TryGetValue(eventType, out var list) ? list : None;
    }

    public EventSubscriptionDescriptor[] GetIntegrationSubscribers(Type eventType) {
        return _integrationSubscribers.TryGetValue(eventType, out var list) ? list : None;
    }

    // OrderBy is a stable sort, so consumers that share an Order keep their generator-emitted sequence.
    private static Dictionary<Type, EventSubscriptionDescriptor[]> Freeze(
        Dictionary<Type, List<EventSubscriptionDescriptor>> source) {
        var result = new Dictionary<Type, EventSubscriptionDescriptor[]>(source.Count);
        foreach (var (type, list) in source)
            result[type] = list.OrderBy(static descriptor => descriptor.Order).ToArray();

        return result;
    }
}
