namespace Elarion.Scheduling.EntityFrameworkCore;

/// <summary>Options for the EF Core scheduler-claims coordinator and its retention purge.</summary>
public sealed class SchedulerClaimsOptions {
    /// <summary>
    /// How long a claim row is kept before the purge worker deletes it. Must comfortably exceed the largest
    /// dedupe window (that is, the largest fixed-rate/fixed-delay interval) of any coordinated job — a purged
    /// claim can no longer suppress a duplicate within its window. Defaults to 7 days.
    /// </summary>
    public TimeSpan ClaimRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How often the purge worker runs. Defaults to 1 hour.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Maximum claim rows deleted per purge pass. Defaults to 5000.</summary>
    public int PurgeBatchSize { get; set; } = 5000;
}
