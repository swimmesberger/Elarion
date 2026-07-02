using Elarion.Abstractions.Resilience;

namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Invokes a generated scheduled job delegate without runtime reflection.
/// </summary>
/// <remarks>
/// The source generator emits delegates of this shape so the runtime scheduler can invoke
/// methods and runtime job classes without reflection or dynamic dispatch.
/// </remarks>
public delegate ValueTask ScheduledJobInvokeDelegate(
    IServiceProvider serviceProvider,
    object? payload,
    IScheduledJobContext context,
    CancellationToken ct);

/// <summary>
/// Immutable source-generated metadata and invocation logic for one scheduled job.
/// </summary>
/// <remarks>
/// Application code normally does not construct descriptors directly; they are emitted by the
/// scheduler source generator. Manual descriptors are useful in tests or advanced host wiring.
/// </remarks>
public sealed record ScheduledJobDescriptor {
    /// <summary>The stable unique job name.</summary>
    public required string Name { get; init; }

    /// <summary>The concrete runtime job type for jobs scheduled through <see cref="IJobScheduler"/>.</summary>
    public Type? JobType { get; init; }

    /// <summary>The runtime payload type for jobs scheduled through <see cref="IJobScheduler"/>.</summary>
    public Type? PayloadType { get; init; }

    /// <summary>
    /// Recurring/startup schedule for compile-time known jobs, or null for runtime-only jobs.
    /// </summary>
    public ScheduledJobSchedule? Schedule { get; init; }

    /// <summary>Optional key used to serialize jobs that share the same external resource.</summary>
    public string? Group { get; init; }

    /// <summary>Controls how overlapping occurrences of the same job are handled.</summary>
    public ScheduledJobOverlap Overlap { get; init; } = ScheduledJobOverlap.Skip;

    /// <summary>Controls how recurring grid schedules handle missed in-process occurrences.</summary>
    public ScheduledJobMisfirePolicy MisfirePolicy { get; init; } = ScheduledJobMisfirePolicy.FireOnce;

    /// <summary>
    /// Maximum concurrently executing occurrences for this job when concurrent execution is allowed.
    /// <c>0</c> means no job-local cap.
    /// </summary>
    public int MaxConcurrentRuns { get; init; }

    /// <summary>
    /// Whether the job runs: a boolean literal or a <c>${Config:Key}</c> placeholder,
    /// evaluated per occurrence. Null means always enabled.
    /// </summary>
    public string? Enabled { get; init; }

    /// <summary>
    /// Where occurrences execute on a multi-node deployment: once per occurrence cluster-wide
    /// (<see cref="JobPlacement.Cluster"/>, the default) or on every node
    /// (<see cref="JobPlacement.EveryNode"/>, for jobs maintaining process-local state).
    /// </summary>
    public JobPlacement Placement { get; init; } = JobPlacement.Cluster;

    /// <summary>
    /// Optional resilience policy used to wrap this job invocation inline.
    /// </summary>
    /// <remarks>
    /// Runtime one-off jobs can override or defer resilience through <see cref="ScheduledJobOptions"/>.
    /// </remarks>
    public ResiliencePolicyReference? ResiliencePolicy { get; init; }

    /// <summary>The generated strongly typed invocation delegate.</summary>
    public required ScheduledJobInvokeDelegate InvokeAsync { get; init; }

    /// <summary>True when this descriptor can be scheduled dynamically through <see cref="IJobScheduler"/>.</summary>
    public bool SupportsRuntimeScheduling => JobType is not null && PayloadType is not null;
}
