namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Controls how runtime job resilience policies are applied.
/// </summary>
public enum ScheduledJobResilienceMode {
    /// <summary>
    /// Execute retries inside the current scheduler run through the resilience pipeline.
    /// Retry delays keep the run active and the same <see cref="ScheduledJobRunHandle.RunId"/> represents all attempts.
    /// </summary>
    Inline,

    /// <summary>
    /// Schedule retries as future scheduler attempts so concurrency slots are released between attempts.
    /// The logical <see cref="ScheduledJobRunHandle.JobId"/> remains stable, but every attempt receives a new run id.
    /// </summary>
    DeferredRetry
}
