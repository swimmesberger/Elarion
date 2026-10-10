namespace Elarion.EntityFrameworkCore.LeasedWork;

/// <summary>
/// Retry delays for a failed leased work row. The result becomes the finalize's visibility deadline
/// (<c>now + delay</c>), so a failing row is not claimed again until it elapses and never blocks the head of the
/// queue.
/// </summary>
public static class LeasedWorkBackoff {
    /// <summary>
    /// Exponential backoff: <c>baseDelay × 2^(attempt-1)</c>, capped at <paramref name="maxDelay"/>.
    /// </summary>
    /// <param name="attempt">The attempt that just failed, starting at 1.</param>
    /// <param name="baseDelay">The delay after the first failure. Zero or negative means retry immediately.</param>
    /// <param name="maxDelay">The ceiling.</param>
    /// <returns>The delay before the next attempt.</returns>
    public static TimeSpan Exponential(int attempt, TimeSpan baseDelay, TimeSpan maxDelay) {
        if (baseDelay <= TimeSpan.Zero) return TimeSpan.Zero;

        // Shift on ticks with a guarded exponent so a large attempt count can never overflow into a negative delay.
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var scaled = baseDelay.Ticks * (1L << exponent);
        if (scaled <= 0 || scaled > maxDelay.Ticks) return maxDelay;

        return TimeSpan.FromTicks(scaled);
    }

    /// <summary>
    /// A fixed ladder: the <paramref name="attempt"/>-th step, repeating the last step once the ladder is exhausted
    /// (for example 1 min, 5 min, 15 min, 1 h, 4 h, 12 h, 24 h). A <paramref name="retryAfter"/> hint from the
    /// remote side (an HTTP <c>Retry-After</c>) wins when it is longer.
    /// </summary>
    /// <param name="attempt">The attempt that just failed, starting at 1.</param>
    /// <param name="steps">The ladder; must not be empty.</param>
    /// <param name="retryAfter">An optional minimum delay requested by the remote side.</param>
    /// <returns>The delay before the next attempt.</returns>
    public static TimeSpan Ladder(int attempt, IReadOnlyList<TimeSpan> steps, TimeSpan? retryAfter = null) {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0) throw new ArgumentException("The backoff ladder needs at least one step.", nameof(steps));

        var delay = steps[Math.Clamp(attempt, 1, steps.Count) - 1];
        return retryAfter is { } hint && hint > delay ? hint : delay;
    }
}
