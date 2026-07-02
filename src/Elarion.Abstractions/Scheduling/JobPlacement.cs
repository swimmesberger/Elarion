namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Where a recurring job's occurrences execute when the application runs on more than one node.
/// </summary>
/// <remarks>
/// Placement is a compile-time property of the job, declared on <see cref="ScheduledJobAttribute.Placement"/>.
/// It only changes behavior when a cross-instance <see cref="IScheduledOccurrenceCoordinator"/> is registered
/// (see <c>docs/decisions/0025-distributed-scheduler-coordination.md</c>); on a single node both values behave
/// identically. One-time startup schedules and runtime one-off jobs always execute locally regardless of this
/// value.
/// </remarks>
public enum JobPlacement {
    /// <summary>
    /// The default: each occurrence executes on exactly one node cluster-wide — the node that wins the
    /// occurrence claim. Right for business rhythms whose effect lives in shared state (reports, purges,
    /// reconciliations); running them per node would duplicate the work.
    /// </summary>
    Cluster,

    /// <summary>
    /// Every node executes every occurrence, with no coordination. Right for jobs that maintain
    /// <b>process-local state</b> — refreshing an in-memory lookup table or cache, pruning node-local
    /// resources — where suppressing the run on nine of ten nodes would leave those nodes stale.
    /// </summary>
    EveryNode
}
