using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace Elarion.EntityFrameworkCore.LeasedWork;

/// <summary>
/// How <see cref="LeasedWorkDbSetExtensions.FinalizeClaimAsync{TRow}(Microsoft.EntityFrameworkCore.DbSet{TRow}, Guid, Guid, LeasedWorkFinalize{TRow}, CancellationToken)"/>
/// ends a claim: the consumer's setters, the retry visibility, and the condition under which the setters still apply.
/// </summary>
/// <typeparam name="TRow">The leased work row type.</typeparam>
public sealed record LeasedWorkFinalize<TRow> where TRow : class, ILeasedWorkRow {
    /// <summary>The consumer's setters (completion marker, error, attempts); must not set the lease columns.</summary>
    public Action<UpdateSettersBuilder<TRow>>? Update { get; init; }

    /// <summary>The earliest time the row may be claimed again (a retry backoff), or <see langword="null"/> for at once.</summary>
    public DateTimeOffset? VisibleAfterUtc { get; init; }

    /// <summary>
    /// A condition the row must still meet for <see cref="Update"/> to apply — for work that must not overwrite what a
    /// concurrent writer did while this worker held no lock (<c>m =&gt; m.State == State.Pending</c>). When it no longer
    /// holds, the claim is released without <see cref="Update"/> and the outcome is
    /// <see cref="LeasedWorkFinalizeOutcome.Ineligible"/>. <see langword="null"/> always applies the update.
    /// </summary>
    public Expression<Func<TRow, bool>>? StillEligible { get; init; }
}

/// <summary>The outcome of a finalize with a <see cref="LeasedWorkFinalize{TRow}"/>.</summary>
public enum LeasedWorkFinalizeOutcome {
    /// <summary>The setters applied and the claim ended.</summary>
    Finalized,

    /// <summary>
    /// The row no longer met <see cref="LeasedWorkFinalize{TRow}.StillEligible"/>: the claim ended without the setters, and
    /// the row is visible at once.
    /// </summary>
    Ineligible,

    /// <summary>The claim was already lost (the lease expired and another worker took the row, or the row is gone).</summary>
    ClaimLost
}
