namespace Elarion.Streams;

/// <summary>What a new <see cref="StreamHub{T}"/> subscriber receives before the live elements.</summary>
public enum StreamReplay {
    /// <summary>Live elements only.</summary>
    None,

    /// <summary>The most recent retained element, then live — the <c>BehaviorSubject</c> greeting.</summary>
    Latest,

    /// <summary>Everything the replay ring retains, then live.</summary>
    Available,
}

/// <summary>
/// Behaviour when a subscriber's buffer is full — the fan-out rule that one slow consumer must never
/// stall the others is the subscriber's own choice here.
/// </summary>
public enum StreamOverflowMode {
    /// <summary>
    /// Drop the oldest buffered element (conflation). The default: right for latest-wins state; the
    /// drop is visible to sequenced consumers as a <see cref="StreamItem{T}.Sequence"/> jump.
    /// </summary>
    DropOldest,

    /// <summary>
    /// Backpressure the publisher: <see cref="StreamHub{T}.PublishAsync"/> does not complete until this
    /// subscriber has room. Gap-free; only meaningful for trusted in-process consumers — a stalled
    /// subscriber stalls the producer.
    /// </summary>
    Wait,

    /// <summary>
    /// Fail the subscriber: its enumeration throws <see cref="StreamLaggedException"/> and it is removed
    /// (the Akka <c>BroadcastHub</c> kill-slow-consumer strategy). The publisher is never delayed.
    /// </summary>
    Cancel,
}

/// <summary>Per-subscriber options for <see cref="StreamHub{T}"/> subscriptions.</summary>
public sealed class StreamSubscribeOptions {
    /// <summary>What to replay before live elements. Default <see cref="StreamReplay.Latest"/>.</summary>
    public StreamReplay Replay { get; init; } = StreamReplay.Latest;

    /// <summary>
    /// Resume after a previously observed <see cref="StreamItem{T}.Sequence"/>: replays every retained
    /// element newer than the value, then continues live. When the gap has outrun the replay ring the
    /// subscriber still gets everything retained — the loss shows as a sequence jump, never a silent
    /// hole. Overrides <see cref="Replay"/> when set.
    /// </summary>
    public long? ResumeAfterSequence { get; init; }

    /// <summary>Buffered elements between publisher and this subscriber. Default 64.</summary>
    public int BufferCapacity { get; init; } = 64;

    /// <summary>Behaviour when the buffer is full. Default <see cref="StreamOverflowMode.DropOldest"/>.</summary>
    public StreamOverflowMode Overflow { get; init; } = StreamOverflowMode.DropOldest;
}
