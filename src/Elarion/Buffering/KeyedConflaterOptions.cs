namespace Elarion.Buffering;

/// <summary>Configuration for a <see cref="KeyedConflater{TKey, TValue}"/>.</summary>
public sealed class KeyedConflaterOptions {
    /// <summary>
    /// The per-key publish floor: each key emits at most once per this interval. The first post of an idle
    /// key emits immediately (leading edge); posts inside the window conflate to the latest value, which
    /// emits when the window elapses (trailing edge). Must be positive; default 1 second.
    /// </summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The clock behind <see cref="MinInterval"/> — swap in a fake for deterministic tests.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
