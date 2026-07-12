namespace Elarion.Streams;

/// <summary>Configuration for a <see cref="StreamHub{T}"/>.</summary>
public sealed class StreamHubOptions {
    /// <summary>
    /// How many published elements the hub retains for replay (the ring buffer behind
    /// <see cref="StreamReplay.Latest"/>/<see cref="StreamReplay.Available"/> and
    /// <see cref="StreamSubscribeOptions.ResumeAfterSequence"/>). <c>1</c> (default) is the
    /// <c>BehaviorSubject</c> shape — new subscribers greet with the current value; raise it to make
    /// reconnects resumable across the retained window; <c>0</c> disables replay entirely.
    /// </summary>
    public int ReplayCapacity { get; init; } = 1;
}
