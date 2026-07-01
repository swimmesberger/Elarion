namespace Elarion.Idempotency.EntityFrameworkCore;

/// <summary>Options for the background purge of expired idempotency records.</summary>
public sealed class IdempotencyPurgeOptions {
    /// <summary>How often the purge worker runs. Default 1 hour.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromHours(1);
}
