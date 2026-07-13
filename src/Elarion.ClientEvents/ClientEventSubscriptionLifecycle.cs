using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.ClientEvents;

/// <summary>
/// Tracks per-(topic, scope) subscriber counts and drives the producer-side lifecycle: the
/// <see cref="IClientEventInterest"/> pull check, and the topic's
/// <see cref="IClientEventSubscriptionObserver"/> callbacks — a per-subscriber greeting on attach, and
/// debounced interest transitions (active on the first subscriber; inactive only after the last one leaves
/// and the topic's linger elapses, so a browser reload never bounces a producer).
/// </summary>
/// <remarks>
/// Observer callbacks run detached from the subscribe path (a slow observer must never delay the stream)
/// on a fresh DI scope per callback, serialized per (topic, scope) in state-transition order: an interest
/// transition is always observed before the greeting that follows it, and a linger-elapsed <c>inactive</c>
/// is always observed before the <c>active</c> of a resubscribe that raced it — the observer's last
/// callback always reflects the current interest. Exceptions are logged, never surfaced to the client.
/// </remarks>
internal sealed class ClientEventSubscriptionLifecycle(
    ClientEventTopicCatalog catalog,
    IElarionJsonSerialization serialization,
    IServiceScopeFactory scopeFactory,
    ILogger<ClientEventSubscriptionLifecycle>? logger = null,
    TimeProvider? timeProvider = null) : IClientEventInterest, IDisposable {
    private readonly ILogger<ClientEventSubscriptionLifecycle> _logger =
        logger ?? NullLogger<ClientEventSubscriptionLifecycle>.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<ClientEventSubscription, InterestState> _interest = [];
    // The per-(topic, scope) observer dispatch chains: the tail every new callback appends behind. Entries are
    // removed once their chain drains, so an idle key holds no memory.
    private readonly Dictionary<ClientEventSubscription, Task> _dispatchTails = [];
    private readonly Lock _lock = new();

    public bool HasSubscribers(string topic, ClientEventScope scope) {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        lock (_lock) {
            return _interest.TryGetValue(new ClientEventSubscription { Topic = topic, Scope = scope }, out var state)
                && state.Count > 0;
        }
    }

    public void OnSubscribed(ClientEventSubscription subscription, ChannelWriter<ClientEventEnvelope> writer) {
        var topic = catalog.FindByName(subscription.Topic);
        lock (_lock) {
            if (!_interest.TryGetValue(subscription, out var state)) {
                state = new InterestState();
                _interest[subscription] = state;
            }
            state.Count += 1;
            // "Active" is the observer-visible epoch, not the raw count: a resubscribe within the linger
            // cancels the pending inactive signal and must NOT re-fire active — interest never went away.
            var becameActive = !state.Active;
            state.Active = true;
            state.LingerTimer?.Dispose();
            state.LingerTimer = null;

            if (topic?.ObserverType is not { } observerType) {
                return;
            }

            var sink = new ClientEventSubscriberSink(subscription, writer, catalog, serialization);
            // Detached from the subscribe path by design; observed inside (catch-all + logging).
            EnqueueDispatch(subscription,
                () => DispatchSubscribedAsync(observerType, subscription, sink, becameActive));
        }
    }

    public void OnUnsubscribed(ClientEventSubscription subscription) {
        lock (_lock) {
            if (!_interest.TryGetValue(subscription, out var state) || state.Count == 0) {
                return;
            }
            state.Count -= 1;
            if (state.Count > 0) {
                return;
            }

            var topic = catalog.FindByName(subscription.Topic);
            if (topic?.ObserverType is not { } observerType) {
                _interest.Remove(subscription);
                return;
            }

            // The reload-debounce: report inactive only if nobody resubscribed within the linger.
            state.LingerTimer?.Dispose();
            state.LingerTimer = _timeProvider.CreateTimer(
                _ => OnLingerElapsed(subscription, observerType),
                state: null, topic.InterestLinger, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnLingerElapsed(ClientEventSubscription subscription, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type observerType) {
        lock (_lock) {
            if (!_interest.TryGetValue(subscription, out var state) || state.Count > 0) {
                return;
            }
            state.LingerTimer?.Dispose();
            _interest.Remove(subscription);
            EnqueueDispatch(subscription,
                () => DispatchInterestChangedAsync(observerType, subscription, active: false));
        }
    }

    /// <summary>
    /// Appends an observer callback to the subscription's dispatch chain. Must be called under
    /// <see cref="_lock"/>: taking the chain tail inside the same lock as the state transition that triggered
    /// the callback is what makes dispatch order match state-transition order — a linger-elapsed inactive
    /// always runs before the active/greeting of a resubscribe that raced it, and a second first-subscriber's
    /// greeting never overtakes the first's active.
    /// </summary>
    private void EnqueueDispatch(ClientEventSubscription subscription, Func<Task> callback) {
        var tail = _dispatchTails.TryGetValue(subscription, out var current) ? current : Task.CompletedTask;
        var chained = RunChainedAsync(tail, callback);
        _dispatchTails[subscription] = chained;
        // Detached by design (like the dispatch itself); RemoveDrainedTailAsync never throws.
        _ = RemoveDrainedTailAsync(subscription, chained);
    }

    private static async Task RunChainedAsync(Task tail, Func<Task> callback) {
        // ForceYielding keeps the callback's synchronous prefix off the enqueuing caller (which holds _lock);
        // SuppressThrowing is a defensive guard — callbacks route through DispatchAsync's catch-all, so a
        // chain never faults, but a faulted predecessor must never wedge the key either.
        await tail.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
        await callback().ConfigureAwait(false);
    }

    private async Task RemoveDrainedTailAsync(ClientEventSubscription subscription, Task chained) {
        await chained.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (_lock) {
            // Only the task that is still the tail removes the entry — a newer append supersedes it.
            if (_dispatchTails.TryGetValue(subscription, out var current) && ReferenceEquals(current, chained)) {
                _dispatchTails.Remove(subscription);
            }
        }
    }

    private async Task DispatchSubscribedAsync(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type observerType, ClientEventSubscription subscription, IClientEventSubscriberSink sink, bool becameActive) {
        // Sequenced: a producer warms up on the interest transition before it is asked to greet.
        if (becameActive) {
            await DispatchInterestChangedAsync(observerType, subscription, active: true);
        }
        await DispatchAsync(observerType, subscription,
            (observer, ct) => observer.OnSubscribedAsync(subscription, sink, ct));
    }

    private Task DispatchInterestChangedAsync(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type observerType, ClientEventSubscription subscription, bool active) =>
        DispatchAsync(observerType, subscription,
            (observer, ct) => observer.OnInterestChangedAsync(subscription, active, ct));

    private async Task DispatchAsync(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type observerType,
        ClientEventSubscription subscription,
        Func<IClientEventSubscriptionObserver, CancellationToken, ValueTask> callback) {
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var observer = (IClientEventSubscriptionObserver)ActivatorUtilities.GetServiceOrCreateInstance(
                scope.ServiceProvider, observerType);
            // Callbacks are short-lived and detached from the (already-returned) subscribe call, so there is
            // no caller-scoped token to thread; observers cancel via their own dependencies' lifetimes.
            await callback(observer, CancellationToken.None);
        }
        catch (Exception exception) {
            _logger.LogError(exception,
                "Client-event subscription observer {Observer} failed for topic {Topic}.",
                observerType.Name, subscription.Topic);
        }
    }

    public void Dispose() {
        lock (_lock) {
            foreach (var state in _interest.Values) {
                state.LingerTimer?.Dispose();
            }
            _interest.Clear();
        }
    }

    private sealed class InterestState {
        public int Count;
        public bool Active;
        public ITimer? LingerTimer;
    }
}
