using System.Diagnostics;
using System.Text.Json;
using Elarion.Abstractions.Idempotency;
using Elarion.Abstractions.Messaging;
using Elarion.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.Messaging.Outbox;

/// <summary>The result of attempting one persisted consumer delivery.</summary>
public enum OutboxDispatchOutcome
{
    /// <summary>The targeted consumer ran to completion.</summary>
    Delivered,

    /// <summary>
    /// The delivery could not be dispatched and never will be — its stored consumer id and event type do not resolve
    /// to one compatible registered consumer, or its payload deserialized to <see langword="null"/>. Retrying would
    /// only spin, so the delivery worker parks it for inspection rather than retrying it.
    /// </summary>
    Unresolvable
}

/// <summary>
/// Resolves and invokes the integration-event consumers for a persisted <see cref="OutboxMessage"/> without runtime
/// type discovery beyond constructing the typed event context.
/// </summary>
/// <remarks>
/// Built once as a singleton from the registered <see cref="EventSubscriptionDescriptor"/>s. The event type is
/// resolved from the targeted consumer descriptor. A delivery whose stored consumer id and event type do not resolve
/// to one compatible descriptor — or whose payload deserializes to <see langword="null"/> — is
/// <see cref="OutboxDispatchOutcome.Unresolvable"/>: it can never be delivered (an event-type rename or dropped
/// consumer would otherwise silently discard every in-flight event), so it is logged at <see cref="LogLevel.Error"/>
/// and parked for inspection by the delivery worker rather than being silently finalized as delivered.
/// </remarks>
public sealed class OutboxEventDispatcher
{
    private readonly ILogger<OutboxEventDispatcher> _logger;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly Dictionary<string, EventSubscriptionDescriptor> _consumersById = new(StringComparer.Ordinal);

    /// <summary>Builds the integration-event consumer index from the registered descriptors.</summary>
    public OutboxEventDispatcher(
        IEnumerable<EventSubscriptionDescriptor> descriptors,
        OutboxOptions options,
        IElarionJsonSerialization jsonSerialization,
        ILogger<OutboxEventDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(jsonSerialization);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _serializerOptions = options.SerializerOptions ?? jsonSerialization.Options;

        foreach (var descriptor in descriptors)
        {
            if (descriptor.Plane is not EventPlane.Integration || descriptor.InvokeAsync is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(descriptor.ConsumerId)) {
                throw new InvalidOperationException(
                    $"Integration-event consumer '{descriptor.ServiceType}' has no stable ConsumerId.");
            }

            if (!_consumersById.TryAdd(descriptor.ConsumerId, descriptor)) {
                throw new InvalidOperationException(
                    $"Integration-event consumer id '{descriptor.ConsumerId}' is registered more than once.");
            }
        }
    }

    /// <summary>
    /// Deserializes the parent message and invokes exactly the consumer named by the delivery.
    /// </summary>
    /// <returns>
    /// <see cref="OutboxDispatchOutcome.Delivered"/> when the consumers ran; <see cref="OutboxDispatchOutcome.Unresolvable"/>
    /// when the stored consumer id and event type resolve to no compatible descriptor or the payload deserializes to
    /// <see langword="null"/> — both logged at <see cref="LogLevel.Error"/> because a retry can never resolve them.
    /// </returns>
    public async ValueTask<OutboxDispatchOutcome> DispatchAsync(
        IServiceProvider serviceProvider,
        OutboxDelivery delivery,
        CancellationToken ct)
    {
        var message = delivery.Message;
        if (!_consumersById.TryGetValue(delivery.ConsumerId, out var descriptor)
            || descriptor.EventType.FullName != message.EventType)
        {
            _logger.LogError(
                "Outbox delivery {DeliveryId} for message {MessageId} targets unknown consumer '{ConsumerId}' or an incompatible event type '{EventType}'; parking it for inspection.",
                delivery.Id,
                message.Id,
                delivery.ConsumerId,
                message.EventType);
            return OutboxDispatchOutcome.Unresolvable;
        }

        var eventType = descriptor.EventType;

        var instance = JsonSerializer.Deserialize(message.Payload, eventType, _serializerOptions);
        if (instance is null)
        {
            _logger.LogError(
                "Outbox message {MessageId} of type '{EventType}' deserialized to null; parking it for inspection.",
                message.Id,
                message.EventType);
            return OutboxDispatchOutcome.Unresolvable;
        }

        // Seed the outbox row's id as the delivery scope's idempotency key: the inbox decorator on handler-form
        // consumers (ADR-0022) claims it per (consumer, message), and it is stable across redeliveries — unlike
        // the correlation id, which is a tracing identifier. Soft-resolved so a host without idempotency wiring
        // simply delivers un-deduped, as before the inbox existed.
        serviceProvider.GetService<IIdempotencyKeySeed>()?.Seed(message.Id.ToString("N"));

        var context = OutboxEventContext.Create(instance, eventType, message.CorrelationId, message.Id);
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await descriptor.InvokeAsync!(serviceProvider, instance, context, ct).ConfigureAwait(false);
            EventTelemetry.RecordConsumer(
                eventType.Name, descriptor.ServiceType.Name, "ok",
                Stopwatch.GetElapsedTime(startTimestamp));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EventTelemetry.RecordConsumer(
                eventType.Name, descriptor.ServiceType.Name, "exception",
                Stopwatch.GetElapsedTime(startTimestamp));
            throw;
        }

        return OutboxDispatchOutcome.Delivered;
    }
}

/// <summary>
/// Constructs the typed <see cref="IEventContext{TEvent}"/> the generated consumer delegates may cast to, for an event
/// type known only at runtime.
/// </summary>
internal static class OutboxEventContext
{
    public static IEventContext Create(object message, Type eventType, Guid correlationId, Guid messageId) =>
        // The generated subscriber delegate casts the context to IEventContext<TEvent> when the consumer declares a
        // typed context parameter, so the concrete type argument must match the runtime event type. The type is kept
        // alive by its descriptor, so this generic construction is safe under trimming.
        (IEventContext)Activator.CreateInstance(
            typeof(OutboxEventContext<>).MakeGenericType(eventType),
            message,
            correlationId,
            messageId)!;
}

/// <summary>The delivery-tier <see cref="IEventContext{TEvent}"/> for an outbox-delivered integration event.</summary>
internal sealed class OutboxEventContext<TEvent>(TEvent message, Guid correlationId, Guid messageId) : IEventContext<TEvent>
{
    public TEvent Message { get; } = message;

    public Guid CorrelationId { get; } = correlationId;

    public Guid? MessageId { get; } = messageId;

    public EventPlane Plane => EventPlane.Integration;

    object IEventContext.Message => Message!;
}
