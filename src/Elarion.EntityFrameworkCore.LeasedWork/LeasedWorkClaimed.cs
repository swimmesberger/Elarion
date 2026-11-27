using System.Collections;

namespace Elarion.EntityFrameworkCore.LeasedWork;

/// <summary>
/// The rows one claim won, in queue order, together with what the claim saw: how many candidates its select found and
/// how many of the won rows it gave up as <see cref="LeasedWorkClaim{TRow}.Exhausted"/>.
/// </summary>
/// <remarks>
/// An empty claim does not mean the queue is drained — every candidate may have been given up or taken by a concurrent
/// worker. <see cref="Candidates"/> says it: loop while it is not zero, and stop when the select found nothing.
/// </remarks>
/// <example>
/// <code>
/// LeasedWorkClaimed&lt;Delivery&gt; claimed;
/// do {
///     claimed = await db.Deliveries.ClaimPendingAsync(Claim(), ct);
///     foreach (var delivery in claimed) await DeliverAsync(delivery, ct);
/// } while (claimed.Candidates &gt; 0);
/// </code>
/// </example>
/// <typeparam name="T">The claimed row type, or the projection of it.</typeparam>
public sealed class LeasedWorkClaimed<T> : IReadOnlyList<T> {
    private readonly IReadOnlyList<T> _rows;

    internal LeasedWorkClaimed(IReadOnlyList<T> rows, int candidates, int givenUp) {
        _rows = rows;
        Candidates = candidates;
        GivenUp = givenUp;
    }

    /// <summary>A claim that found no candidate: the queue holds no claimable row right now.</summary>
    internal static LeasedWorkClaimed<T> None { get; } = new([], 0, 0);

    /// <summary>
    /// How many rows the candidate select found (at most the batch size). Zero means the queue held no claimable row;
    /// anything else means there may be more work, even when this claim won nothing.
    /// </summary>
    public int Candidates { get; }

    /// <summary>
    /// How many won rows this claim gave up as exhausted (finalized with <see cref="LeasedWorkClaim{TRow}.GiveUp"/> and
    /// not returned). Counted whether or not <see cref="LeasedWorkClaim{TRow}.OnGivenUp"/> is set.
    /// </summary>
    public int GivenUp { get; }

    /// <inheritdoc />
    public int Count => _rows.Count;

    /// <inheritdoc />
    public T this[int index] => _rows[index];

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => _rows.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
