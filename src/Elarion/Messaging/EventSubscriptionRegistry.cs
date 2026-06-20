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
    private readonly Dictionary<Type, EventSubscriptionDescriptor> _domainResponders;

    public EventSubscriptionRegistry(IEnumerable<EventSubscriptionDescriptor> descriptors) {
        var domainSubscribers = new Dictionary<Type, List<EventSubscriptionDescriptor>>();
        var integrationSubscribers = new Dictionary<Type, List<EventSubscriptionDescriptor>>();
        var domainResponders = new Dictionary<Type, EventSubscriptionDescriptor>();

        foreach (var descriptor in descriptors) {
            if (descriptor.IsResponder) {
                if (descriptor.Plane is not EventPlane.Domain) {
                    throw new InvalidOperationException(
                        $"Responder for '{descriptor.EventType}' must be a domain request; integration events cannot have responders.");
                }

                if (!domainResponders.TryAdd(descriptor.EventType, descriptor)) {
                    throw new InvalidOperationException(
                        $"More than one responder is registered for request type '{descriptor.EventType}'.");
                }

                continue;
            }

            var target = descriptor.Plane is EventPlane.Domain ? domainSubscribers : integrationSubscribers;
            if (!target.TryGetValue(descriptor.EventType, out var list)) {
                list = [];
                target[descriptor.EventType] = list;
            }

            list.Add(descriptor);
        }

        _domainSubscribers = Freeze(domainSubscribers);
        _integrationSubscribers = Freeze(integrationSubscribers);
        _domainResponders = domainResponders;
    }

    public EventSubscriptionDescriptor[] GetDomainSubscribers(Type eventType) =>
        _domainSubscribers.TryGetValue(eventType, out var list) ? list : None;

    public EventSubscriptionDescriptor[] GetIntegrationSubscribers(Type eventType) =>
        _integrationSubscribers.TryGetValue(eventType, out var list) ? list : None;

    public EventSubscriptionDescriptor? GetDomainResponder(Type requestType) =>
        _domainResponders.GetValueOrDefault(requestType);

    // OrderBy is a stable sort, so consumers that share an Order keep their generator-emitted sequence.
    private static Dictionary<Type, EventSubscriptionDescriptor[]> Freeze(
        Dictionary<Type, List<EventSubscriptionDescriptor>> source) {
        var result = new Dictionary<Type, EventSubscriptionDescriptor[]>(source.Count);
        foreach (var (type, list) in source) {
            result[type] = list.OrderBy(static descriptor => descriptor.Order).ToArray();
        }

        return result;
    }
}
