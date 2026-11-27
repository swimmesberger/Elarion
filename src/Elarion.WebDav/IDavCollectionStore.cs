namespace Elarion.WebDav;

/// <summary>
/// The published-collection store and its change journal (ADR-0111, spec 0006 FR-004–FR-008, FR-020, FR-021): the
/// objects an application published, one counter per collection that every change increments, and a journal of
/// changes the sync token reads from, pruned while the collection changes.
/// </summary>
/// <remarks>
/// <para>
/// Writes take part in the caller's transaction where the implementation can (the EF store joins the
/// <c>DbContext</c>'s), so a published object is visible exactly when the caller commits. A publish whose bytes equal the
/// stored ones writes nothing. Every other change increments the counter under the collection's row lock, so tokens
/// strictly increase across nodes, and appends one journal row per changed member.
/// </para>
/// <para>
/// Enumerations hold a reader open on the store's connection until they end: do not start another call on the same
/// store while enumerating.
/// </para>
/// </remarks>
public interface IDavCollectionStore {
    /// <summary>The state of each of <paramref name="addresses"/>, in their order; a collection nothing was published to is at counter 0.</summary>
    ValueTask<IReadOnlyList<DavCollectionState>> GetStatesAsync(IReadOnlyList<DavCollectionAddress> addresses,
        CancellationToken cancellationToken = default);

    /// <summary>Every member of a collection without content, ordered by name.</summary>
    IAsyncEnumerable<DavMember> ListMembersAsync(Guid collectionId, CancellationToken cancellationToken = default);

    /// <summary>The members named <paramref name="names"/> that exist, with their content when <paramref name="includeContent"/>.</summary>
    IAsyncEnumerable<DavStoredObject> GetObjectsAsync(Guid collectionId, IReadOnlyCollection<string> names, bool includeContent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The members whose recorded time span may overlap <c>[start, end]</c>, with content, ordered by name: those without a
    /// time, and those whose span starts at or before <paramref name="end"/> and ends at or after <paramref name="start"/>
    /// (or is unbounded). A <see langword="null"/> bound is open. The caller applies the exact test.
    /// </summary>
    IAsyncEnumerable<DavStoredObject> GetCandidatesAsync(Guid collectionId, DateTimeOffset? start, DateTimeOffset? end,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The journal rows of a collection after <paramref name="sinceSeq"/>, or <see langword="null"/> when the token is no
    /// longer valid: below the collection's low-water mark, or beyond its counter.
    /// </summary>
    ValueTask<DavChangeSet?> GetChangesAsync(Guid collectionId, long sinceSeq, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces one object; writes nothing when the stored entity tag is equal.</summary>
    ValueTask<DavPublishOutcome> PublishAsync(DavCollectionAddress address, DavObjectContent content,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the member <paramref name="name"/>; false when it does not exist.</summary>
    ValueTask<bool> RemoveAsync(DavCollectionAddress address, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes <paramref name="contents"/> the collection's whole content: adds what is new, replaces what changed and
    /// removes what the set lacks, under one lock, recording each change.
    /// </summary>
    ValueTask<DavReplaceOutcome> ReplaceAsync(DavCollectionAddress address, IAsyncEnumerable<DavObjectContent> contents,
        CancellationToken cancellationToken = default);
}
