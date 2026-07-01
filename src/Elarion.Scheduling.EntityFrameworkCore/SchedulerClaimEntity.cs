namespace Elarion.Scheduling.EntityFrameworkCore;

/// <summary>
/// One claimed recurring-job occurrence: the fence that makes an occurrence execute on exactly one node
/// (ADR-0025). The composite primary key <c>(JobName, OccurrenceUtc)</c> serializes concurrent claimants —
/// the row is inserted with <c>ON CONFLICT DO NOTHING</c>, and the node whose insert affected a row won.
/// Rows are purged after <see cref="SchedulerClaimsOptions.ClaimRetention"/>.
/// </summary>
public sealed class SchedulerClaimEntity {
    /// <summary>The recurring job's descriptor name.</summary>
    public required string JobName { get; init; }

    /// <summary>The claimed occurrence's due instant in UTC.</summary>
    public required DateTimeOffset OccurrenceUtc { get; init; }

    /// <summary>When the claim was made, in UTC.</summary>
    public required DateTimeOffset ClaimedAtUtc { get; init; }
}
