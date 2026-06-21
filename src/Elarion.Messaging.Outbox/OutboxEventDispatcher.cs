using System.Text.Json;
using Elarion.Abstractions.Messaging;

namespace Elarion.Messaging.Outbox;

/// <summary>
/// Resolves and invokes the integration-event consumers for a persisted <see cref="OutboxMessage"/> without runtime
/// type discovery beyond constructing the typed event context.
/// </summary>
/// <remarks>
/// Built once as a singleton from the registered <see cref="EventSubscriptionDescriptor"/>s. The event type is
/// resolved from the registered consumers' descriptors (by <see cref="Type.FullName"/>), so a message whose type has
/// no consumers is a no-op and is finalized as delivered.
/// </remarks>
public sealed class OutboxEventDispatcher
{
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly Dictionary<string, Type> _typeByName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, EventSubscriptionDescriptor[]> _consumersByType = new();

    /// <summary>Builds the integration-event consumer index from the registered descriptors.</summary>
    public OutboxEventDispatcher(IEnumerable<EventSubscriptionDescriptor> descriptors, OutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(options);
        _serializerOptions = options.SerializerOptions;

        var byType = new Dictionary<Type, List<EventSubscriptionDescriptor>>();
        foreach (var descriptor in descriptors)
        {
            if (descriptor.Plane is not EventPlane.Integration || descriptor.InvokeAsync is null)
            {
                continue;
            }

            var fullName = descriptor.EventType.FullName;
            if (fullName is null)
            {
                continue;
            }

            _typeByName[fullName] = descriptor.EventType;
            if (!byType.TryGetValue(descriptor.EventType, out var list))
            {
                list = [];
                byType[descriptor.EventType] = list;
            }

            list.Add(descriptor);
        }

        foreach (var (type, list) in byType)
        {
            _consumersByType[type] = list.OrderBy(static descriptor => descriptor.Order).ToArray();
        }
    }

    /// <summary>
    /// Deserializes <paramref name="message"/> and invokes every registered integration consumer on
    /// <paramref name="serviceProvider"/>. Returns without effect when the event type has no consumers. Any consumer
    /// exception propagates so the worker can mark the message failed and retry the whole message later.
    /// </summary>
    public async ValueTask DispatchAsync(
        IServiceProvider serviceProvider,
        OutboxMessage message,
        CancellationToken ct)
    {
        if (!_typeByName.TryGetValue(message.EventType, out var eventType)
            || !_consumersByType.TryGetValue(eventType, out var consumers))
        {
            return;
        }

        var instance = JsonSerializer.Deserialize(message.Payload, eventType, _serializerOptions);
        if (instance is null)
        {
            return;
        }

        var context = OutboxEventContext.Create(instance, eventType, message.CorrelationId);
        foreach (var descriptor in consumers)
        {
            await descriptor.InvokeAsync!(serviceProvider, instance, context, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Constructs the typed <see cref="IEventContext{TEvent}"/> the generated consumer delegates may cast to, for an event
/// type known only at runtime.
/// </summary>
internal static class OutboxEventContext
{
    public static IEventContext Create(object message, Type eventType, Guid correlationId) =>
        // The generated subscriber delegate casts the context to IEventContext<TEvent> when the consumer declares a
        // typed context parameter, so the concrete type argument must match the runtime event type. The type is kept
        // alive by its descriptor, so this generic construction is safe under trimming.
        (IEventContext)Activator.CreateInstance(
            typeof(OutboxEventContext<>).MakeGenericType(eventType),
            message,
            correlationId)!;
}

/// <summary>The delivery-tier <see cref="IEventContext{TEvent}"/> for an outbox-delivered integration event.</summary>
internal sealed class OutboxEventContext<TEvent>(TEvent message, Guid correlationId) : IEventContext<TEvent>
{
    public TEvent Message { get; } = message;

    public Guid CorrelationId { get; } = correlationId;

    public EventPlane Plane => EventPlane.Integration;

    object IEventContext.Message => Message!;
}
