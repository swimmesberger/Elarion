using Elarion.Abstractions;
using Elarion.Abstractions.Messaging;

namespace Elarion.Messaging;

/// <summary>
/// In-process <see cref="IDomainEventBus"/>: dispatches domain events inline within the caller's
/// DI scope so consumers share the caller's scoped services and transaction.
/// </summary>
/// <remarks>
/// Registered scoped; the injected <see cref="IServiceProvider"/> is the caller's scope, so every
/// consumer resolved here observes the same scoped <c>DbContext</c> as the publisher.
/// </remarks>
internal sealed class InMemoryDomainEventBus(
    IServiceProvider serviceProvider,
    EventSubscriptionRegistry registry
) : IDomainEventBus {
    public async ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IDomainEvent {
        ArgumentNullException.ThrowIfNull(@event);

        var subscribers = registry.GetDomainSubscribers(typeof(TEvent));
        if (subscribers.Length == 0) {
            return;
        }

        var context = new EventContext<TEvent>(@event, Guid.NewGuid(), EventPlane.Domain);
        List<Exception>? failures = null;
        foreach (var descriptor in subscribers) {
            try {
                await descriptor.InvokeAsync!(serviceProvider, @event, context, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                // Run every subscriber, then surface all failures so one publish fails atomically.
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null) {
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
    }

    public async ValueTask<Result<TResponse>> RequestAsync<TRequest, TResponse>(
        TRequest request,
        CancellationToken ct = default)
        where TRequest : IDomainEvent {
        ArgumentNullException.ThrowIfNull(request);

        var responder = registry.GetDomainResponder(typeof(TRequest))
            ?? throw new InvalidOperationException(
                $"No responder is registered for request type '{typeof(TRequest)}'.");

        var context = new EventContext<TRequest>(request, Guid.NewGuid(), EventPlane.Domain);
        var result = await responder.InvokeRequestAsync!(serviceProvider, request, context, ct)
            .ConfigureAwait(false);
        return (Result<TResponse>)result;
    }
}
