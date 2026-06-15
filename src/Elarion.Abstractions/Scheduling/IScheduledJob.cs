namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Defines a runtime-schedulable job with a strongly typed in-memory payload.
/// </summary>
/// <remarks>
/// Implementations must also be annotated with <see cref="ScheduledJobAttribute"/> so the
/// source generator can register the job type and emit a direct invocation delegate.
/// </remarks>
/// <typeparam name="TPayload">The payload type supplied when scheduling the job.</typeparam>
public interface IScheduledJob<in TPayload> {
    /// <summary>
    /// Executes one scheduled job occurrence with its payload and scheduler context.
    /// </summary>
    /// <remarks>
    /// Always pass <paramref name="ct"/> to I/O and long-running work. Scheduler cancellation,
    /// timeout, and shutdown are cooperative.
    /// </remarks>
    ValueTask ExecuteAsync(TPayload payload, IScheduledJobContext context, CancellationToken ct);
}
