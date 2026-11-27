using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;

namespace Elarion.WebDav;

/// <summary>
/// An <see cref="IDavCollectionStore"/> in process memory, for tests and single-node development: it keeps the same
/// counters, journal and pruning as the EF store, but joins no transaction and loses everything on restart.
/// </summary>
public sealed class InMemoryDavCollectionStore(IOptions<WebDavOptions> options, TimeProvider timeProvider) : IDavCollectionStore {
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Collection> _collections = [];

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DavCollectionState>> GetStatesAsync(IReadOnlyList<DavCollectionAddress> addresses,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(addresses);
        lock (_gate) {
            return ValueTask.FromResult<IReadOnlyList<DavCollectionState>>([
                .. addresses.Select(address => _collections.TryGetValue(address.Id, out var collection)
                    ? new DavCollectionState(address, collection.Seq, collection.LowWater)
                    : new DavCollectionState(address, 0, 0)),
            ]);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DavMember> ListMembersAsync(Guid collectionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        List<DavMember> members;
        lock (_gate) {
            members = _collections.TryGetValue(collectionId, out var collection)
                ? [.. collection.Objects.Values.OrderBy(static o => o.Name, StringComparer.Ordinal).Select(static o => ToMember(o))]
                : [];
        }

        foreach (var member in members) {
            cancellationToken.ThrowIfCancellationRequested();
            yield return member;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DavStoredObject> GetObjectsAsync(Guid collectionId, IReadOnlyCollection<string> names, bool includeContent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(names);
        List<DavStoredObject> found = [];
        lock (_gate) {
            if (_collections.TryGetValue(collectionId, out var collection))
                foreach (var name in names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    if (collection.Objects.TryGetValue(name, out var stored))
                        found.Add(includeContent ? stored : stored with { Content = ReadOnlyMemory<byte>.Empty });
        }

        foreach (var item in found) {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DavStoredObject> GetCandidatesAsync(Guid collectionId, DateTimeOffset? start, DateTimeOffset? end,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        List<DavStoredObject> found = [];
        lock (_gate) {
            if (_collections.TryGetValue(collectionId, out var collection))
                foreach (var (name, stored) in collection.Objects.OrderBy(static pair => pair.Key, StringComparer.Ordinal)) {
                    var (rangeStart, rangeEnd) = collection.Ranges[name];
                    if (rangeStart is null
                        || ((end is null || rangeStart <= end) && (start is null || rangeEnd is null || rangeEnd >= start)))
                        found.Add(stored);
                }
        }

        foreach (var item in found) {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<DavChangeSet?> GetChangesAsync(Guid collectionId, long sinceSeq, CancellationToken cancellationToken = default) {
        lock (_gate) {
            if (!_collections.TryGetValue(collectionId, out var collection))
                return ValueTask.FromResult(sinceSeq == 0 ? new DavChangeSet(0, []) : null);
            if (sinceSeq < collection.LowWater || sinceSeq > collection.Seq) return ValueTask.FromResult<DavChangeSet?>(null);
            return ValueTask.FromResult<DavChangeSet?>(new DavChangeSet(collection.Seq,
                [.. collection.Journal.Where(change => change.Change.Seq > sinceSeq).Select(static change => change.Change)]));
        }
    }

    /// <inheritdoc />
    public ValueTask<DavPublishOutcome> PublishAsync(DavCollectionAddress address, DavObjectContent content,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate) {
            var collection = GetOrAdd(address);
            if (collection.Objects.TryGetValue(content.Name, out var existing) && existing.ETag == content.ETag)
                return ValueTask.FromResult(DavPublishOutcome.Unchanged);
            var kind = existing is null ? DavChangeKind.Added : DavChangeKind.Changed;
            Put(collection, content);
            Record(collection, content.Name, kind);
            Prune(collection);
            return ValueTask.FromResult(kind == DavChangeKind.Added ? DavPublishOutcome.Added : DavPublishOutcome.Changed);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(DavCollectionAddress address, string name, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate) {
            if (!_collections.TryGetValue(address.Id, out var collection) || !collection.Objects.Remove(name)) return ValueTask.FromResult(false);
            collection.Ranges.Remove(name);
            Record(collection, name, DavChangeKind.Removed);
            Prune(collection);
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public async ValueTask<DavReplaceOutcome> ReplaceAsync(DavCollectionAddress address, IAsyncEnumerable<DavObjectContent> contents,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(contents);
        var incoming = new Dictionary<string, DavObjectContent>(StringComparer.Ordinal);
        await foreach (var content in contents.WithCancellation(cancellationToken).ConfigureAwait(false)) incoming[content.Name] = content;

        lock (_gate) {
            var collection = GetOrAdd(address);
            int added = 0, changed = 0, removed = 0;
            foreach (var content in incoming.Values) {
                if (collection.Objects.TryGetValue(content.Name, out var existing) && existing.ETag == content.ETag) continue;
                var kind = existing is null ? DavChangeKind.Added : DavChangeKind.Changed;
                if (kind == DavChangeKind.Added) added++;
                else changed++;
                Put(collection, content);
                Record(collection, content.Name, kind);
            }

            foreach (var name in collection.Objects.Keys.Where(name => !incoming.ContainsKey(name)).ToList()) {
                collection.Objects.Remove(name);
                collection.Ranges.Remove(name);
                Record(collection, name, DavChangeKind.Removed);
                removed++;
            }

            if (added + changed + removed > 0) Prune(collection);
            return new DavReplaceOutcome(added, changed, removed);
        }
    }

    private Collection GetOrAdd(DavCollectionAddress address) {
        if (!_collections.TryGetValue(address.Id, out var collection)) _collections[address.Id] = collection = new Collection();
        return collection;
    }

    private static void Put(Collection collection, DavObjectContent content) {
        collection.Objects[content.Name] = new DavStoredObject {
            Name = content.Name,
            Uid = content.Uid,
            ETag = content.ETag,
            ContentType = content.ContentType,
            Size = content.Content.Length,
            Content = content.Content.ToArray(),
        };
        collection.Ranges[content.Name] = (content.RangeStart, content.RangeEnd);
    }

    private void Record(Collection collection, string name, DavChangeKind kind) {
        collection.Seq++;
        collection.Journal.Add((new DavChange(name, kind, collection.Seq), timeProvider.GetUtcNow()));
    }

    private void Prune(Collection collection) {
        var settings = options.Value;
        var cutoff = timeProvider.GetUtcNow() - settings.JournalRetention;
        var keepAbove = collection.Seq - settings.MaxJournalRowsPerCollection;
        var pruned = collection.Journal.Where(row => row.Change.Seq <= keepAbove || row.At < cutoff).ToList();
        if (pruned.Count == 0) return;
        collection.LowWater = Math.Max(collection.LowWater, pruned.Max(static row => row.Change.Seq));
        collection.Journal.RemoveAll(row => row.Change.Seq <= collection.LowWater);
    }

    private static DavMember ToMember(DavStoredObject stored) => new() {
        Name = stored.Name, ETag = stored.ETag, ContentType = stored.ContentType, Size = stored.Size,
    };

    private sealed class Collection {
        public long Seq { get; set; }

        public long LowWater { get; set; }

        public Dictionary<string, DavStoredObject> Objects { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, (DateTimeOffset? Start, DateTimeOffset? End)> Ranges { get; } = new(StringComparer.Ordinal);

        public List<(DavChange Change, DateTimeOffset At)> Journal { get; } = [];
    }
}
