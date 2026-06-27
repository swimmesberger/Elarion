using System.Collections.Concurrent;

namespace Elarion.Settings.InProcess;

/// <summary>
/// Thread-safe in-memory <see cref="ISettingsStore"/>. The shipped default backend: usable on its own for
/// single-process apps, tests, and development, and the reference implementation of the sink contract. Writes
/// signal the supplied <see cref="ISettingsChangePublisher"/> so watchers observe changes.
/// </summary>
public sealed class InProcessSettingsStore(ISettingsChangePublisher publisher, TimeProvider timeProvider) : ISettingsStore {
    private readonly ConcurrentDictionary<(SettingsScope Scope, string Key), SettingEntry> _entries = new();

    // Serializes the read-modify-write of the version check; in-memory writes are infrequent relative to reads.
    private readonly object _writeLock = new();

    /// <inheritdoc />
    public ValueTask<string?> GetAsync(SettingsScope scope, string key, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(key);
        return ValueTask.FromResult(_entries.TryGetValue((scope, key), out var entry) ? entry.Value : null);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SettingEntry>> GetAllAsync(SettingsScope scope, CancellationToken cancellationToken = default) {
        var entries = new List<SettingEntry>();
        foreach (var (entryKey, entry) in _entries) {
            if (entryKey.Scope == scope) {
                entries.Add(entry);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<SettingEntry>>(entries);
    }

    /// <inheritdoc />
    public ValueTask<SettingWriteResult> SetAsync(
        SettingsScope scope,
        string key,
        string? value,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(key);

        SettingWriteResult result;
        var changed = false;
        lock (_writeLock) {
            var entryKey = (scope, key);
            if (_entries.TryGetValue(entryKey, out var existing)) {
                if (expectedVersion is { } expected && expected != existing.Version) {
                    result = SettingWriteResult.ConcurrencyConflict;
                } else {
                    var updated = existing with {
                        Value = value,
                        Version = existing.Version + 1,
                        UpdatedOnUtc = timeProvider.GetUtcNow()
                    };
                    _entries[entryKey] = updated;
                    result = SettingWriteResult.Success(updated.Version);
                    changed = true;
                }
            } else if (expectedVersion is { } expected && expected != 0) {
                result = SettingWriteResult.ConcurrencyConflict;
            } else {
                var created = new SettingEntry(key, value, timeProvider.GetUtcNow(), Version: 1);
                _entries[entryKey] = created;
                result = SettingWriteResult.Success(created.Version);
                changed = true;
            }
        }

        if (changed) {
            // Published outside the lock so change callbacks never run while the write lock is held.
            publisher.Publish(scope, key);
        }

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(
        SettingsScope scope,
        string key,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(key);

        var removed = false;
        lock (_writeLock) {
            var entryKey = (scope, key);
            if (_entries.TryGetValue(entryKey, out var existing) &&
                (expectedVersion is not { } expected || expected == existing.Version)) {
                _entries.TryRemove(entryKey, out _);
                removed = true;
            }
        }

        if (removed) {
            publisher.Publish(scope, key);
        }

        return ValueTask.FromResult(removed);
    }
}
