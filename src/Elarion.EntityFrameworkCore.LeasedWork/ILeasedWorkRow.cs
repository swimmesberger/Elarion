namespace Elarion.EntityFrameworkCore.LeasedWork;

/// <summary>
/// The lease columns of a leased work row (ADR-0073): one row in a queue-shaped table that at most one worker
/// processes at a time.
/// </summary>
/// <remarks>
/// <para>
/// Implement the members as ordinary (implicit) mapped properties with these exact names: the claim and finalize
/// queries and <see cref="LeasedWorkModelBuilderExtensions.HasElarionLeasedWork{TRow}"/> resolve them by name on the
/// entity, so an explicit interface implementation is not mapped.
/// </para>
/// <para>
/// The interface carries only the lease. Everything else a queue needs — the pending/completed marker, the attempt
/// counter, the error text, a deadline, a target — stays the consumer's own column and enters the primitive as an
/// eligibility filter (<see cref="LeasedWorkClaim{TRow}.Eligible"/>) or as setters on claim and finalize.
/// </para>
/// </remarks>
public interface ILeasedWorkRow {
    /// <summary>The row's primary key; the identity a claim is finalized by.</summary>
    Guid Id { get; }

    /// <summary>
    /// The token of the worker that currently holds the claim, or <see langword="null"/> when no worker holds it.
    /// Written only by the leased-work primitive.
    /// </summary>
    Guid? LockId { get; }

    /// <summary>
    /// While <see cref="LockId"/> is set, when the claim lapses and the row becomes reclaimable. After a finalize
    /// it is the visibility deadline (a retry backoff): the row is not claimed again before it. <see langword="null"/>
    /// means immediately eligible. Written only by the leased-work primitive.
    /// </summary>
    DateTimeOffset? LockedUntilUtc { get; }
}
