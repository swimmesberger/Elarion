using System.Collections.Concurrent;

namespace Elarion.Devices;

/// <summary>
/// In-memory <see cref="IPairingCodeStore"/> for tests and single-node development. Claiming is a
/// dictionary remove, so it is atomic and single-use like the durable store.
/// </summary>
public sealed class InMemoryPairingCodeStore : IPairingCodeStore {
    private readonly ConcurrentDictionary<string, PairingCodeEntry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<bool> TryCreateAsync(PairingCodeEntry entry, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(entry);
        return ValueTask.FromResult(_entries.TryAdd(entry.CodeHash, entry));
    }

    /// <inheritdoc />
    public ValueTask<PairingCodeEntry?> ClaimAsync(string codeHash, DateTimeOffset now, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(codeHash);
        // Expired entries are left for the sweep (matching the EF store's claim, which only
        // deletes live rows); the conditional remove keeps concurrent claims single-winner.
        if (!_entries.TryGetValue(codeHash, out var entry) || entry.ExpiresAt <= now) {
            return ValueTask.FromResult<PairingCodeEntry?>(null);
        }

        var claimed = _entries.TryRemove(new KeyValuePair<string, PairingCodeEntry>(codeHash, entry));
        return ValueTask.FromResult(claimed ? entry : null);
    }

    /// <inheritdoc />
    public ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default) {
        var removed = 0;
        foreach (var (hash, entry) in _entries) {
            if (entry.ExpiresAt <= now && _entries.TryRemove(new KeyValuePair<string, PairingCodeEntry>(hash, entry))) {
                removed++;
            }
        }

        return ValueTask.FromResult(removed);
    }
}
