using System.Linq.Expressions;
using System.Runtime.ExceptionServices;
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
    /// reflect <see cref="LeasedWorkClaim{TRow}.OnClaim"/>. A won row matching <see cref="LeasedWorkClaim{TRow}.Exhausted"/>
    /// is given up before the read and never returned.
    /// </remarks>
    /// <param name="rows">The leased work table.</param>
    /// <param name="claim">What to claim and the lease to stamp.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <typeparam name="TRow">The leased work row type.</typeparam>
    /// <returns>
    /// The claimed rows, possibly empty. Empty does not mean the queue is drained: every candidate may have been given
    /// up or won by a concurrent worker. Loop until the candidate select finds nothing, or poll on a timer.
    /// </returns>
    public static async Task<IReadOnlyList<TRow>> ClaimPendingAsync<TRow>(
        this DbSet<TRow> rows,
        LeasedWorkClaim<TRow> claim,
        CancellationToken ct)
        where TRow : class, ILeasedWorkRow =>
        await ClaimCoreAsync(rows, claim, static claimed => claimed, ct).ConfigureAwait(false);

    /// <summary>
    /// Claims like <see cref="ClaimPendingAsync{TRow}(DbSet{TRow}, LeasedWorkClaim{TRow}, CancellationToken)"/> and
    /// returns the won rows projected by <paramref name="select"/> in the same query, so related data (a navigation, a
    /// joined column) arrives with the claim instead of in a second query. Keep the row's <c>Id</c> in the projection:
    /// every finalize needs it.
    /// </summary>
    /// <example>
    /// <code>
    /// var claimed = await db.ImageMetadata.ClaimPendingAsync(claim,
    ///     m =&gt; new { m.Id, m.Attempts, m.Attachment.BlobId }, ct);
    /// </code>
    /// </example>
    /// <param name="rows">The leased work table.</param>
    /// <param name="claim">What to claim and the lease to stamp.</param>
    /// <param name="select">The projection of each won row.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <typeparam name="TRow">The leased work row type.</typeparam>
    /// <typeparam name="TResult">The projected type.</typeparam>
    /// <returns>The claimed rows' projections in queue order, possibly empty.</returns>
    public static async Task<IReadOnlyList<TResult>> ClaimPendingAsync<TRow, TResult>(
        this DbSet<TRow> rows,
        LeasedWorkClaim<TRow> claim,
        Expression<Func<TRow, TResult>> select,
        CancellationToken ct)
        where TRow : class, ILeasedWorkRow {
        ArgumentNullException.ThrowIfNull(select);
        return await ClaimCoreAsync(rows, claim, claimed => claimed.Select(select), ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TResult>> ClaimCoreAsync<TRow, TResult>(
        DbSet<TRow> rows,
        LeasedWorkClaim<TRow> claim,
        Func<IQueryable<TRow>, IQueryable<TResult>> shape,
        CancellationToken ct)
        where TRow : class, ILeasedWorkRow {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(claim.BatchSize);
        if (claim.Exhausted is not null && claim.GiveUp is null)
            throw new ArgumentException(
                $"{nameof(LeasedWorkClaim<TRow>.Exhausted)} needs {nameof(LeasedWorkClaim<TRow>.GiveUp)}: a row given up " +
                "without leaving the eligible set would be claimed and given up again forever.", nameof(claim));

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

        IReadOnlyList<TRow> givenUp = [];
        if (claim.Exhausted is { } exhausted)
            givenUp = await GiveUpExhaustedAsync(rows, claim, candidateIds, exhausted, ct).ConfigureAwait(false);

        var won = await shape(claim.OrderBy(rows.AsNoTracking()
                    .Where(row => candidateIds.Contains(row.Id) && row.LockId == lockId))
                .ThenBy(row => row.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (givenUp.Count > 0) await ReportGivenUpAsync(rows, claim, candidateIds, givenUp, ct).ConfigureAwait(false);
        return won;
    }

    // Ends the claim of every won row that has used up its attempts, with the consumer's give-up setters, before the
    // won rows are read back — so the worker never sees them. The lease guard keeps another worker's rows untouched.
    // Returns the rows actually given up, as claimed, when OnGivenUp wants them.
    private static async Task<IReadOnlyList<TRow>> GiveUpExhaustedAsync<TRow>(
        DbSet<TRow> rows,
        LeasedWorkClaim<TRow> claim,
        List<Guid> candidateIds,
        Expression<Func<TRow, bool>> exhausted,
        CancellationToken ct)
        where TRow : class, ILeasedWorkRow {
        var lockId = claim.LockId;
        if (claim.OnGivenUp is null) {
            await GiveUpAsync(rows.Where(row => candidateIds.Contains(row.Id) && row.LockId == lockId)).ConfigureAwait(false);
            return [];
        }

        var reported = await rows.AsNoTracking()
            .Where(row => candidateIds.Contains(row.Id) && row.LockId == lockId)
            .Where(exhausted)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (reported.Count == 0) return [];

        // Restricted to the rows read, and the predicate restated: a concurrent writer that made a row eligible again
        // in between keeps it claimed (it is returned as won), and only what this statement gave up is reported.
        var reportedIds = reported.Select(row => row.Id).ToList();
        var givenUp = await GiveUpAsync(rows.Where(row => reportedIds.Contains(row.Id) && row.LockId == lockId))
            .ConfigureAwait(false);
        if (givenUp == reported.Count) return reported;

        var kept = await rows.AsNoTracking()
            .Where(row => reportedIds.Contains(row.Id) && row.LockId == lockId)
            .Select(row => row.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return reported.Where(row => !kept.Contains(row.Id)).ToList();

        Task<int> GiveUpAsync(IQueryable<TRow> mine) =>
            mine.Where(exhausted).ExecuteUpdateAsync(
                setters => {
                    claim.GiveUp!(setters);
                    setters
                        .SetProperty(row => row.LockId, (Guid?)null)
                        .SetProperty(row => row.LockedUntilUtc, (DateTimeOffset?)null);
                },
                ct);
    }

    // Reports every given-up row, even when an earlier callback threw: the give-up is already written, so a skipped
    // report would be lost. A failed report fails the claim; the won rows are released first rather than left stamped
    // until their lease lapses — the bookkeeping OnClaim wrote stays, as after a crashed worker.
    private static async Task ReportGivenUpAsync<TRow>(
        DbSet<TRow> rows,
        LeasedWorkClaim<TRow> claim,
        List<Guid> candidateIds,
        IReadOnlyList<TRow> givenUp,
        CancellationToken ct)
        where TRow : class, ILeasedWorkRow {
        List<Exception>? failures = null;
        foreach (var row in givenUp) {
            try {
                await claim.OnGivenUp!(row, ct).ConfigureAwait(false);
            }
            catch (Exception ex) {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is null) return;

        // CancellationToken.None: a cancelled token is a likely cause of the failure, and the release is one short
        // guarded statement that must still run.
        await rows.ReleaseClaimsAsync(candidateIds, claim.LockId, update: null, CancellationToken.None)
            .ConfigureAwait(false);
        if (failures is [var single]) ExceptionDispatchInfo.Throw(single);
        throw new AggregateException("Reporting given-up leased work rows failed.", failures);
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
    /// Ends this worker's claim on one row as <paramref name="finalize"/> says — its setters, its retry visibility —
    /// applying the setters only while the row still meets <see cref="LeasedWorkFinalize{TRow}.StillEligible"/>.
    /// </summary>
    /// <remarks>
    /// Work done without a lock can race a concurrent writer: a backfill that read a file must not overwrite the values
    /// a newer upload already wrote. <see cref="LeasedWorkFinalize{TRow}.StillEligible"/> states once what the row must
    /// still look like, instead of guarding every setter. When it no longer holds, the claim is still ended (the lease
    /// cleared) but nothing else is written. The row is then visible at once, whatever
    /// <see cref="LeasedWorkFinalize{TRow}.VisibleAfterUtc"/> says: that backoff belonged to the result just discarded,
    /// and whoever changed the row decides whether it is still work (through <see cref="LeasedWorkClaim{TRow}.Eligible"/>).
    /// </remarks>
    /// <param name="rows">The leased work table.</param>
    /// <param name="id">The claimed row's <see cref="ILeasedWorkRow.Id"/>.</param>
    /// <param name="lockId">The token the row was claimed with.</param>
    /// <param name="finalize">The setters, the retry visibility and the eligibility guard.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <typeparam name="TRow">The leased work row type.</typeparam>
    /// <returns>Whether the setters applied, the row was no longer eligible, or the claim was lost.</returns>
    public static async Task<LeasedWorkFinalizeOutcome> FinalizeClaimAsync<TRow>(
        this DbSet<TRow> rows,
        Guid id,
        Guid lockId,
        LeasedWorkFinalize<TRow> finalize,
        CancellationToken ct)
        where TRow : class, ILeasedWorkRow {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(finalize);

        var claimed = WhereClaimedBy(rows, id, lockId);
        var guarded = finalize.StillEligible is { } eligible ? claimed.Where(eligible) : claimed;
        var visibleAfter = finalize.VisibleAfterUtc;
        var applied = await guarded
            .ExecuteUpdateAsync(
                setters => {
                    finalize.Update?.Invoke(setters);
                    setters
                        .SetProperty(row => row.LockId, (Guid?)null)
                        .SetProperty(row => row.LockedUntilUtc, visibleAfter);
                },
                ct)
            .ConfigureAwait(false);
        if (applied > 0) return LeasedWorkFinalizeOutcome.Finalized;
        if (finalize.StillEligible is null) return LeasedWorkFinalizeOutcome.ClaimLost;

        var released = await rows.ReleaseClaimAsync(id, lockId, ct).ConfigureAwait(false);
        return released ? LeasedWorkFinalizeOutcome.Ineligible : LeasedWorkFinalizeOutcome.ClaimLost;
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
    /// Gives the still-held claims on <paramref name="ids"/> back in one statement, applying <paramref name="update"/>
    /// to each — typically a worker shutting down mid-batch, taking back the attempt the claim counted
    /// (<c>s =&gt; s.SetProperty(r =&gt; r.Attempts, r =&gt; r.Attempts - 1)</c>) because it was never made. Rows whose
    /// claim this worker already ended or lost are left alone.
    /// </summary>
    /// <param name="rows">The leased work table.</param>
    /// <param name="ids">The claimed rows to give back.</param>
    /// <param name="lockId">The token the rows were claimed with.</param>
    /// <param name="update">Setters undoing the claim's bookkeeping, or <see langword="null"/>; must not set the lease columns.</param>
    /// <param name="ct">The cancellation token — on shutdown, a fresh one: the worker's own is already cancelled.</param>
    /// <typeparam name="TRow">The leased work row type.</typeparam>
    /// <returns>How many claims were given back.</returns>
    public static Task<int> ReleaseClaimsAsync<TRow>(
        this DbSet<TRow> rows,
        IEnumerable<Guid> ids,
        Guid lockId,
        Action<UpdateSettersBuilder<TRow>>? update,
        CancellationToken ct)
        where TRow : class, ILeasedWorkRow {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(ids);

        var idList = ids.ToList();
        if (idList.Count == 0) return Task.FromResult(0);

        return rows.Where(row => idList.Contains(row.Id) && row.LockId == lockId)
            .ExecuteUpdateAsync(
                setters => {
                    update?.Invoke(setters);
                    setters
                        .SetProperty(row => row.LockId, (Guid?)null)
                        .SetProperty(row => row.LockedUntilUtc, (DateTimeOffset?)null);
                },
                ct);
    }

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
