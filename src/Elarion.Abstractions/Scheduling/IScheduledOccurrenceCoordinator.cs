namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Identifies one recurring-job occurrence a scheduler node is about to execute, for cross-instance
/// claiming (see <see cref="IScheduledOccurrenceCoordinator"/>).
/// </summary>
public readonly record struct ScheduledOccurrence {
    /// <summary>The recurring job's descriptor name.</summary>
    public required string JobName { get; init; }

    /// <summary>The occurrence's due instant in UTC.</summary>
    public required DateTimeOffset DueTimeUtc { get; init; }

    /// <summary>
    /// How the occurrence deduplicates across nodes. <see langword="null"/> claims the exact
    /// <c>(job, due-time)</c> slot — right for cron schedules, whose instants are wall-clock deterministic on
    /// every node. A window claims "at most one run per window": the claim succeeds only when no claim for
    /// the job exists within this span before <see cref="DueTimeUtc"/> — right for fixed-rate/fixed-delay
    /// schedules, whose due times are anchored per node and never align exactly.
    /// </summary>
    public TimeSpan? DedupeWindow { get; init; }
}

/// <summary>
/// Decides which node executes a recurring-job occurrence when the same job set runs on several instances.
/// The scheduler asks to claim each occurrence right before executing it; exactly one node wins and runs,
/// the others record the occurrence as skipped (<c>claimed-elsewhere</c>) and continue their local chains.
/// See <c>docs/decisions/0025-distributed-scheduler-coordination.md</c>.
/// </summary>
/// <remarks>
/// The shipped default (<c>LocalScheduledOccurrenceCoordinator</c> in the <c>Elarion</c> core) always claims,
/// preserving single-node semantics with no I/O. The EF Core/PostgreSQL implementation
/// (<c>Elarion.Scheduling.EntityFrameworkCore</c>) claims through a unique-constrained database row, making
/// recurring jobs fire once per occurrence cluster-wide. Implementations should let failures propagate — the
/// scheduler fails <b>closed</b> (skips the occurrence) when a claim cannot be made, because running anyway
/// would reintroduce the duplication coordination exists to prevent.
/// </remarks>
public interface IScheduledOccurrenceCoordinator {
    /// <summary>
    /// Attempts to claim <paramref name="occurrence"/> for this node. Returns <see langword="true"/> when
    /// this node won the occurrence and must execute it, <see langword="false"/> when another node claimed
    /// it (or a claim within the occurrence's dedupe window already exists).
    /// </summary>
    ValueTask<bool> TryClaimAsync(ScheduledOccurrence occurrence, CancellationToken cancellationToken);
}
