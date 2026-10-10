using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Elarion.EntityFrameworkCore.LeasedWork;

/// <summary>
/// Claims and finalizes leased work rows (ADR-0073): the claim is select → conditional stamp → re-read, and every
/// write after it is guarded on the claiming worker's lease token.
/// </summary>
/// <remarks>
/// <para>
/// No call holds a row lock or a transaction across the work itself. A worker claims a batch in three short
/// statements, does its (possibly slow, external) work with no database resources held, then finalizes each row in
/// one conditional update. A worker that crashes or stalls releases nothing: its stamps lapse at
/// <see cref="ILeasedWorkRow.LockedUntilUtc"/>, the rows become claimable again, and the stalled worker's late
/// finalize matches zero rows and returns <see langword="false"/>. That is why the finalize guard is the invariant —
/// without it an expired worker can complete a row another worker is processing.
/// </para>
/// <para>
/// The statements are plain EF Core LINQ and <c>ExecuteUpdate</c>, so the primitive is provider-neutral; claiming
/// workers on several instances never block each other.
/// </para>
/// </remarks>
public static class LeasedWorkDbSetExtensions {
    /// <summary>
    /// Claims up to <see cref="LeasedWorkClaim{TRow}.BatchSize"/> eligible rows for
    /// <see cref="LeasedWorkClaim{TRow}.LockId"/> and returns the rows this worker actually won, in queue order.
    /// </summary>
    /// <remarks>
    /// The candidate select is advisory; the conditional update, which restates the whole eligibility predicate,
    /// is the arbiter. Concurrent workers that select overlapping candidates each get the subset they stamped, and
    /// no row is returned to two workers for the same lease period. The returned rows are untracked and already
    /// reflect <see cref="LeasedWorkClaim{TRow}.OnClaim"/>.
    /// </remarks>
    /// <param name="rows">The leased work table.</param>
    /// <param name="claim">What to claim and the lease to stamp.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <typeparam name="TRow">The leased work row type.</typeparam>
    /// <returns>The claimed rows, possibly empty.</returns>
    public static async Task<IReadOnlyList<TRow>> ClaimPendingAsync<TRow>(
        this DbSet<TRow> rows,
        LeasedWorkClaim<TRow> claim,
        CancellationToken ct)
        where TRow : class, ILeasedWorkRow {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(claim.BatchSize);

        var lockId = claim.LockId;
        var leaseUntil = claim.LeaseUntilUtc;

        var candidateIds = await claim.OrderBy(WhereClaimable(rows.AsNoTracking(), claim.Eligible, claim.NowUtc))
            .ThenBy(row => row.Id)
            .Take(claim.BatchSize)
            .Select(row => row.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidateIds.Count == 0) return [];

        await WhereClaimable(rows.Where(row => candidateIds.Contains(row.Id)), claim.Eligible, claim.NowUtc)
            .ExecuteUpdateAsync(
                setters => {
                    claim.OnClaim?.Invoke(setters);
                    setters
                        .SetProperty(row => row.LockId, (Guid?)lockId)
                        .SetProperty(row => row.LockedUntilUtc, (DateTimeOffset?)leaseUntil);
                },
                ct)
            .ConfigureAwait(false);

        return await claim.OrderBy(rows.AsNoTracking()
                .Where(row => candidateIds.Contains(row.Id) && row.LockId == lockId))
            .ThenBy(row => row.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ends this worker's claim on one row and applies <paramref name="update"/> in the same statement — but only
    /// while <paramref name="lockId"/> still holds the claim.
    /// </summary>
    /// <remarks>
    /// The lease token is cleared and <see cref="ILeasedWorkRow.LockedUntilUtc"/> becomes
    /// <paramref name="visibleAfterUtc"/>: <see langword="null"/> for a row that is done (or should be claimable at
    /// once), a future instant for a retry backoff. Completion, failure, and attempt bookkeeping are the consumer's
    /// columns, set through <paramref name="update"/>, which must not set the lease columns itself.
    /// </remarks>
    /// <param name="rows">The leased work table.</param>
    /// <param name="id">The claimed row's <see cref="ILeasedWorkRow.Id"/>.</param>
    /// <param name="lockId">The token the row was claimed with.</param>
    /// <param name="update">The consumer's setters (completion marker, error, attempts), or <see langword="null"/>.</param>
    /// <param name="visibleAfterUtc">The earliest time the row may be claimed again, or <see langword="null"/>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <typeparam name="TRow">The leased work row type.</typeparam>
    /// <returns>
    /// <see langword="true"/> when the row was updated; <see langword="false"/> when the claim was lost (the lease
    /// expired and another worker re-stamped the row, or it was released) — the caller must then skip, not assume
    /// success.
    /// </returns>
    public static async Task<bool> FinalizeClaimAsync<TRow>(
        this DbSet<TRow> rows,
        Guid id,
        Guid lockId,
        Action<UpdateSettersBuilder<TRow>>? update,
        DateTimeOffset? visibleAfterUtc,
        CancellationToken ct)
        where TRow : class, ILeasedWorkRow {
        ArgumentNullException.ThrowIfNull(rows);

        var updated = await WhereClaimedBy(rows, id, lockId)
            .ExecuteUpdateAsync(
                setters => {
                    update?.Invoke(setters);
                    setters
                        .SetProperty(row => row.LockId, (Guid?)null)
                        .SetProperty(row => row.LockedUntilUtc, visibleAfterUtc);
                },
                ct)
            .ConfigureAwait(false);
        return updated > 0;
    }

    /// <summary>
    /// Gives a still-held claim back without any bookkeeping, so the row is immediately claimable again — for work
    /// this worker claimed but decided not to start (for example because it no longer owns the row's target).
    /// </summary>
    /// <param name="rows">The leased work table.</param>
    /// <param name="id">The claimed row's <see cref="ILeasedWorkRow.Id"/>.</param>
    /// <param name="lockId">The token the row was claimed with.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <typeparam name="TRow">The leased work row type.</typeparam>
    /// <returns><see langword="true"/> when released; <see langword="false"/> when the claim was already lost.</returns>
    public static Task<bool> ReleaseClaimAsync<TRow>(this DbSet<TRow> rows, Guid id, Guid lockId, CancellationToken ct)
        where TRow : class, ILeasedWorkRow =>
        rows.FinalizeClaimAsync(id, lockId, update: null, visibleAfterUtc: null, ct);

    /// <summary>
    /// Extends a still-held claim to <paramref name="leaseUntilUtc"/> — a heartbeat for work that may outlive the
    /// original lease, renewed before it lapses.
    /// </summary>
    /// <param name="rows">The leased work table.</param>
    /// <param name="id">The claimed row's <see cref="ILeasedWorkRow.Id"/>.</param>
    /// <param name="lockId">The token the row was claimed with.</param>
    /// <param name="leaseUntilUtc">The new lease expiry.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <typeparam name="TRow">The leased work row type.</typeparam>
    /// <returns>
    /// <see langword="true"/> when renewed; <see langword="false"/> when the claim was already lost, in which case the
    /// caller should stop the work if it can.
    /// </returns>
    public static async Task<bool> RenewClaimAsync<TRow>(
        this DbSet<TRow> rows,
        Guid id,
        Guid lockId,
        DateTimeOffset leaseUntilUtc,
        CancellationToken ct)
        where TRow : class, ILeasedWorkRow {
        ArgumentNullException.ThrowIfNull(rows);

        var updated = await WhereClaimedBy(rows, id, lockId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.LockedUntilUtc, (DateTimeOffset?)leaseUntilUtc),
                ct)
            .ConfigureAwait(false);
        return updated > 0;
    }

    private static IQueryable<TRow> WhereClaimable<TRow>(
        IQueryable<TRow> rows,
        Expression<Func<TRow, bool>>? eligible,
        DateTimeOffset now)
        where TRow : class, ILeasedWorkRow {
        if (eligible is not null) rows = rows.Where(eligible);
        return rows.Where(row => row.LockedUntilUtc == null || row.LockedUntilUtc < now);
    }

    private static IQueryable<TRow> WhereClaimedBy<TRow>(IQueryable<TRow> rows, Guid id, Guid lockId)
        where TRow : class, ILeasedWorkRow =>
        rows.Where(row => row.Id == id && row.LockId == lockId);
}
