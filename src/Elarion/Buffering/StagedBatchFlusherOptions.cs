namespace Elarion.Buffering;

/// <summary>Configuration for a <see cref="StagedBatchFlusher{TBatch}"/>.</summary>
public sealed class StagedBatchFlusherOptions {
    /// <summary>
    /// How long <see cref="StagedBatchFlusher{TBatch}.DisposeAsync"/> waits for the final drain (any
    /// in-flight write plus a batch submitted before disposal). Default
    /// <see cref="Timeout.InfiniteTimeSpan"/> — shutdown writes are the last state of departed entities,
    /// so the default waits them out; bound it only when a wedged write target must not block shutdown.
    /// On timeout, dispose stops waiting and returns — the write may still complete in the background.
    /// </summary>
    public TimeSpan DisposeTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>The clock behind <see cref="DisposeTimeout"/> — swap in a fake for deterministic tests.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
