using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace Elarion.EntityFrameworkCore.LeasedWork;

/// <summary>
/// One claim request for <see cref="LeasedWorkDbSetExtensions.ClaimPendingAsync{TRow}"/>: which rows are
/// eligible, in which order, how many, and the lease to stamp on the ones this worker wins.
/// </summary>
/// <typeparam name="TRow">The leased work row type.</typeparam>
/// <example>
/// <code>
/// var claimed = await db.Deliveries.ClaimPendingAsync(new LeasedWorkClaim&lt;Delivery&gt; {
///     LockId = Guid.CreateVersion7(),
///     NowUtc = now,
///     LeaseUntilUtc = now + TimeSpan.FromMinutes(2),
///     BatchSize = 20,
///     Eligible = d => d.CompletedAtUtc == null &amp;&amp; d.Attempts &lt; 8,
///     OrderBy = rows => rows.OrderBy(d => d.CreatedAtUtc),
///     OnClaim = s => s.SetProperty(d => d.Attempts, d => d.Attempts + 1),
/// }, ct);
/// </code>
/// </example>
public sealed record LeasedWorkClaim<TRow> where TRow : class, ILeasedWorkRow {
    /// <summary>
    /// This worker's fresh lease token (one per claim, minted with <see cref="Guid.CreateVersion7()"/>). Every
    /// later finalize, release, or renewal of a claimed row must present it.
    /// </summary>
    public required Guid LockId { get; init; }

    /// <summary>
    /// The current time. A row is claimable only while its <see cref="ILeasedWorkRow.LockedUntilUtc"/> is
    /// <see langword="null"/> or earlier than this — an expired lease or an elapsed retry backoff.
    /// </summary>
    public required DateTimeOffset NowUtc { get; init; }

    /// <summary>
    /// When the stamped lease lapses. Keep it comfortably above the time to process the whole batch, or the tail of a
    /// slow batch becomes reclaimable while this worker still processes it (the finalize guard then rejects this
    /// worker's late write, and the row is processed twice).
    /// </summary>
    public required DateTimeOffset LeaseUntilUtc { get; init; }

    /// <summary>The maximum number of rows to claim. Must be positive.</summary>
    public required int BatchSize { get; init; }

    /// <summary>
    /// The queue order. The primitive appends <c>ThenBy(Id)</c>, so ties resolve deterministically. Pair it with the
    /// claim index declared by <see cref="LeasedWorkModelBuilderExtensions.HasLeasedWork{TRow}"/>.
    /// </summary>
    public required Func<IQueryable<TRow>, IOrderedQueryable<TRow>> OrderBy { get; init; }

    /// <summary>
    /// The consumer's eligibility predicate on its own columns — for example "not completed, under the attempt
    /// limit, deadline not passed, target held". <see langword="null"/> makes every row whose lease is free
    /// eligible. It is applied both to the advisory candidate select and to the conditional stamp, so a row that
    /// stopped being eligible in between is not stamped.
    /// </summary>
    public Expression<Func<TRow, bool>>? Eligible { get; init; }

    /// <summary>
    /// Extra setters applied in the same conditional update that stamps the lease — typically counting the attempt at
    /// claim time (<c>s.SetProperty(r =&gt; r.Attempts, r =&gt; r.Attempts + 1)</c>), so a worker that dies during
    /// the external call still uses up an attempt and a row that crashes every worker eventually stops being
    /// eligible. Must not set the lease columns.
    /// </summary>
    public Action<UpdateSettersBuilder<TRow>>? OnClaim { get; init; }
}
