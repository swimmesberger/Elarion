namespace Elarion.Buffering;

/// <summary>Configuration for a <see cref="WriteBehindBuffer{T}"/>.</summary>
public sealed class WriteBehindBufferOptions {
    /// <summary>
    /// How many buffered items trigger an immediate flush (the batch-size lever — the natural value is
    /// whatever your flush target likes per call, e.g. a bulk-insert batch). Must be at least 1; default 500.
    /// </summary>
    public int MaxItems { get; init; } = 500;

    /// <summary>
    /// The longest an item waits before it is flushed: a one-shot timer armed by the first item after a
    /// flush, so a trickle of samples still reaches the target within this window even when
    /// <see cref="MaxItems"/> is never hit. Default 1 second; <see cref="Timeout.InfiniteTimeSpan"/>
    /// disables interval flushing entirely (count-only).
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The hard bound on buffered (unflushed) items — beyond it the <b>oldest</b> item is dropped, because
    /// the buffer's contract is loss-tolerant samples and memory must stay bounded when the flush target is
    /// slow or down. Must be at least <see cref="MaxItems"/>; default 4 × <see cref="MaxItems"/>.
    /// </summary>
    public int? Capacity { get; init; }

    /// <summary>The clock behind <see cref="FlushInterval"/> — swap in a fake for deterministic tests.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
