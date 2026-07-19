using Elarion.Abstractions.Scheduling;

namespace Elarion.Scheduling;

/// <summary>
/// The single-node default <see cref="IScheduledOccurrenceCoordinator"/>: every occurrence is claimed by the
/// current process, with no I/O — recurring jobs behave exactly as before coordination existed. Replace it
/// with the EF Core/PostgreSQL coordinator (<c>AddElarionSchedulerEntityFrameworkCore</c>) so a multi-node
/// deployment executes each occurrence on exactly one node (ADR-0025).
/// </summary>
public sealed class LocalScheduledOccurrenceCoordinator : IScheduledOccurrenceCoordinator {
    /// <inheritdoc />
    public ValueTask<bool> TryClaimAsync(ScheduledOccurrence occurrence, CancellationToken cancellationToken) {
        return ValueTask.FromResult(true);
    }
}
