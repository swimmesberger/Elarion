using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Elarion.Streams;

/// <summary>
/// A hot, ordered, completable in-memory broadcast: the producer publishes, every subscriber observes the
/// elements <b>in publish order</b> with a hub-assigned contiguous sequence, and new subscribers atomically
/// get replay-then-live (the retained ring and every subsequent publish, with no seam between them — the
/// <c>BehaviorSubject</c> contract). Complete/Fail end every subscription — the signal fire-and-forget
/// events don't have. This is the sequencer-owned counterpart to client events: reach for it only when one
/// live producer owns the stream and element identity matters (ADR-0052); latest-wins hints stay on client
/// events.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single sequencer by design.</b> The intended owner is a single writer — typically an actor, whose
/// mailbox already serializes publishes. Concurrent <see cref="PublishAsync"/> calls are safe (an internal
/// gate serializes them, preserving per-subscriber order), but ordering <em>between</em> concurrent
/// publishers is decided by that gate, not by the callers — and <see cref="Complete"/>/<see cref="Fail"/>
/// must be serialized with publishes by the producer (see their remarks): racing them makes final-element
/// delivery per-subscriber nondeterministic.
/// </para>
/// <para>
/// <b>Lifetime.</b> The hub lives and dies with its owner: an actor whose activation can passivate must
/// <see cref="Complete"/> its hubs in <c>OnDeactivateAsync</c>, so subscribers observe the end and
/// re-subscribe (re-activating the actor) instead of starving on a channel nothing writes to anymore.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type.</typeparam>
public sealed class StreamHub<T> {
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly Queue<StreamItem<T>> _replay;
    private readonly int _replayCapacity;
    private readonly List<Subscriber> _subscribers = [];

    private long _sequence;

    // The ring's newest element, tracked separately so a Latest subscribe is O(1) instead of copying
    // the ring. Only set while retention is on (ReplayCapacity > 0) — Latest on a no-replay hub
    // greets with nothing, by contract.
    private StreamItem<T>? _latest;
    private bool _completed;
    private Exception? _error;

    /// <summary>Creates a hub. See <see cref="StreamHubOptions"/> for the replay-ring size.</summary>
    public StreamHub(StreamHubOptions? options = null) {
        _replayCapacity = options?.ReplayCapacity ?? 1;
        ArgumentOutOfRangeException.ThrowIfNegative(_replayCapacity, nameof(options));
        _replay = new Queue<StreamItem<T>>(Math.Min(_replayCapacity, 16));
    }

    /// <summary>The sequence of the most recent publish; <c>0</c> when nothing was published yet.</summary>
    public long LastSequence {
        get {
            lock (_lock) {
                return _sequence;
            }
        }
    }

    /// <summary>
    /// Current subscriber count — the interest pull for lazy producers (compute/publish only when
    /// someone watches). Node-local like all interest signals.
    /// </summary>
    public int SubscriberCount {
        get {
            lock (_lock) {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>
    /// Publishes the next element to every subscriber, in order. Completes immediately unless a
    /// <see cref="StreamOverflowMode.Wait"/> subscriber's buffer is full — then it waits for that
    /// subscriber (backpressure). Throws <see cref="InvalidOperationException"/> after
    /// <see cref="Complete"/>/<see cref="Fail"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Serialize publishes with completion.</b> The hub's ordering guarantees assume the producer
    /// serializes its <see cref="PublishAsync"/> calls with <see cref="Complete"/>/<see cref="Fail"/> —
    /// naturally true in the intended single-sequencer usage (an actor's mailbox, ADR-0052). A
    /// <see cref="Complete"/>/<see cref="Fail"/> racing a concurrent publish makes delivery of that final
    /// committed element per-subscriber nondeterministic: some live subscribers may observe it and others
    /// not, and a later replay subscriber may see an element a live subscriber missed.
    /// </para>
    /// <para>
    /// <b>Cancellation is only clean before commit.</b> <paramref name="cancellationToken"/> cancels the
    /// publish cleanly only while it is still waiting to enter the hub (the element was not committed).
    /// Once the element is committed — sequence assigned and retained in the replay ring — cancelling
    /// (e.g. while a <see cref="StreamOverflowMode.Wait"/> subscriber applies backpressure) abandons the
    /// remaining deliveries of that element: it exists in replay for new subscribers, but pending
    /// Wait-mode subscribers never receive it.
    /// </para>
    /// </remarks>
    public async ValueTask PublishAsync(T item, CancellationToken cancellationToken = default) {
        // The gate keeps per-subscriber order intact even when a Wait-mode subscriber suspends this
        // publish while another one starts; single-writer owners never contend on it.
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            StreamItem<T> element;
            Subscriber[] snapshot;
            lock (_lock) {
                if (_completed) throw new InvalidOperationException("The stream hub is completed; publishing is over.");

                element = new StreamItem<T>(++_sequence, item);
                if (_replayCapacity > 0) {
                    _latest = element;
                    _replay.Enqueue(element);
                    while (_replay.Count > _replayCapacity) _replay.Dequeue();
                }

                snapshot = [.. _subscribers];
            }

            foreach (var subscriber in snapshot) {
                if (subscriber.Channel.Writer.TryWrite(element)) continue;

                switch (subscriber.Overflow) {
                    case StreamOverflowMode.Wait:
                        // Await room outside the lock; the publish gate preserves element order.
                        while (!subscriber.Channel.Writer.TryWrite(element))
                            if (!await subscriber.Channel.Writer.WaitToWriteAsync(cancellationToken)
                                    .ConfigureAwait(false))
                                break; // subscriber left while we waited

                        break;
                    case StreamOverflowMode.Cancel:
                        subscriber.Channel.Writer.TryComplete(new StreamLaggedException(
                            $"The subscriber lagged more than {subscriber.EffectiveCapacity} elements behind the " +
                            "stream and was cancelled; re-subscribe with ResumeAfterSequence to continue."));
                        Remove(subscriber);
                        break;
                    default:
                        break; // DropOldest is handled inside the bounded channel; TryWrite cannot fail.
                }
            }
        }
        finally {
            _publishGate.Release();
        }
    }

    /// <summary>Ends the stream: every subscription completes normally, further publishes throw.</summary>
    /// <remarks>
    /// Serialize with <see cref="PublishAsync"/> (the producer's job — automatic under the intended
    /// single-sequencer/actor-mailbox usage, ADR-0052): completing concurrently with an in-flight publish
    /// makes delivery of that final committed element per-subscriber nondeterministic — some live
    /// subscribers may observe it, others may not, and a late replay subscriber may see an element a live
    /// subscriber missed.
    /// </remarks>
    public void Complete() {
        Close(null);
    }

    /// <summary>Ends the stream with an error: every subscription (current and future) throws it.</summary>
    /// <remarks>
    /// Serialize with <see cref="PublishAsync"/> (the producer's job — automatic under the intended
    /// single-sequencer/actor-mailbox usage, ADR-0052): failing concurrently with an in-flight publish
    /// makes delivery of that final committed element per-subscriber nondeterministic — some live
    /// subscribers may observe it, others may not, and a late replay subscriber may see an element a live
    /// subscriber missed.
    /// </remarks>
    public void Fail(Exception error) {
        ArgumentNullException.ThrowIfNull(error);
        Close(error);
    }

    /// <summary>
    /// Subscribes and returns the elements without their sequence — the ergonomic in-process form. See
    /// <see cref="SubscribeSequenced"/> for semantics (the subscription attaches now, here too).
    /// </summary>
    public IAsyncEnumerable<T> Subscribe(StreamSubscribeOptions? options = null) {
        return Unwrap(SubscribeSequenced(options));
    }

    private static async IAsyncEnumerable<T> Unwrap(
        IAsyncEnumerable<StreamItem<T>> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return item.Value;
    }

    /// <summary>
    /// Subscribes: atomically replays per <see cref="StreamSubscribeOptions"/> and continues with every
    /// subsequent publish, in order. The subscription attaches <em>now</em> (inside the caller's actor
    /// turn, when called from one); enumerate the result exactly once — disposing the enumerator
    /// unsubscribes. Ends when the hub completes; throws the hub's failure or
    /// <see cref="StreamLaggedException"/> under <see cref="StreamOverflowMode.Cancel"/>.
    /// </summary>
    /// <remarks>
    /// When the replay burst is larger than <see cref="StreamSubscribeOptions.BufferCapacity"/>, the
    /// subscriber's buffer is widened to the burst size so the greeting can never overflow the buffer it
    /// was just written into; that widened capacity is the subscription's effective capacity for its whole
    /// lifetime (overflow decisions and the <see cref="StreamLaggedException"/> message use it).
    /// </remarks>
    public IAsyncEnumerable<StreamItem<T>> SubscribeSequenced(StreamSubscribeOptions? options = null) {
        options ??= new StreamSubscribeOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.BufferCapacity, nameof(options));

        Subscriber subscriber;
        lock (_lock) {
            var replayItems = ReplayFor(options);
            // Size for the replay burst so DropOldest can never evict the greeting it just wrote.
            var capacity = Math.Max(options.BufferCapacity, replayItems.Count);
            var channel = Channel.CreateBounded<StreamItem<T>>(new BoundedChannelOptions(capacity) {
                FullMode = options.Overflow == StreamOverflowMode.DropOldest
                    ? BoundedChannelFullMode.DropOldest
                    : BoundedChannelFullMode.Wait,
                SingleReader = true
            });
            subscriber = new Subscriber(channel, options.Overflow, capacity);
            foreach (var item in replayItems) channel.Writer.TryWrite(item);

            if (_completed)
                channel.Writer.TryComplete(_error);
            else
                _subscribers.Add(subscriber);
        }

        return EnumerateAsync(subscriber);
    }

    private List<StreamItem<T>> ReplayFor(StreamSubscribeOptions options) {
        if (options.ResumeAfterSequence is { } after) {
            // Everything retained that is newer; a gap that outran the ring shows as a sequence jump.
            List<StreamItem<T>> resumed = [];
            foreach (var item in _replay)
                if (item.Sequence > after)
                    resumed.Add(item);

            return resumed;
        }

        return options.Replay switch {
            StreamReplay.Latest when _latest is { } latest => [latest],
            StreamReplay.Available => [.. _replay],
            _ => []
        };
    }

    private async IAsyncEnumerable<StreamItem<T>> EnumerateAsync(
        Subscriber subscriber, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        try {
            await foreach (var item in subscriber.Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally {
            Remove(subscriber);
        }
    }

    private void Close(Exception? error) {
        Subscriber[] snapshot;
        lock (_lock) {
            if (_completed) return;

            _completed = true;
            _error = error;
            snapshot = [.. _subscribers];
            _subscribers.Clear();
        }

        foreach (var subscriber in snapshot) subscriber.Channel.Writer.TryComplete(error);
    }

    private void Remove(Subscriber subscriber) {
        lock (_lock) {
            _subscribers.Remove(subscriber);
        }

        // Wake a publish blocked in WaitToWriteAsync on this subscriber's full Wait-mode buffer: the
        // completed writer makes that wait return false and the publish moves on. Without it the publish
        // would suspend forever while holding the publish gate, wedging every future publish.
        subscriber.Channel.Writer.TryComplete();
    }

    private sealed record Subscriber(
        Channel<StreamItem<T>> Channel,
        StreamOverflowMode Overflow,
        int EffectiveCapacity);
}
