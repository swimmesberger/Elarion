namespace Elarion.AppPasswords;

/// <summary>
/// An <see cref="IAppPasswordStore"/> in process memory: tests and single-node development only, because every app
/// password is lost on restart. Register it with <c>AddElarionInMemoryAppPasswordStore()</c>.
/// </summary>
public sealed class InMemoryAppPasswordStore : IAppPasswordStore {
    private readonly Lock _gate = new();
    private readonly List<AppPasswordRecord> _records = [];

    /// <inheritdoc />
    public ValueTask<bool> TryAddAsync(AppPasswordRecord record, int maxPerUser,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate) {
            if (_records.Exists(existing => string.Equals(existing.Hash, record.Hash, StringComparison.Ordinal)))
                throw new InvalidOperationException("An app password with this hash already exists.");
            if (_records.Count(existing => string.Equals(existing.UserId, record.UserId, StringComparison.Ordinal))
                >= maxPerUser)
                return ValueTask.FromResult(false);
            _records.Add(record);
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AppPasswordInfo>> ListAsync(string userId,
        CancellationToken cancellationToken = default) {
        lock (_gate) {
            IReadOnlyList<AppPasswordInfo> list = _records
                .Where(record => string.Equals(record.UserId, userId, StringComparison.Ordinal))
                .OrderBy(record => record.CreatedAt)
                .ThenBy(record => record.Id)
                .Select(record => record.ToInfo())
                .ToArray();
            return ValueTask.FromResult(list);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string userId, Guid id, CancellationToken cancellationToken = default) {
        lock (_gate) {
            var removed = _records.RemoveAll(record =>
                record.Id == id && string.Equals(record.UserId, userId, StringComparison.Ordinal));
            return ValueTask.FromResult(removed > 0);
        }
    }

    /// <inheritdoc />
    public ValueTask<AppPasswordRecord?> FindByHashAsync(string hash, CancellationToken cancellationToken = default) {
        lock (_gate) {
            return ValueTask.FromResult(
                _records.Find(record => string.Equals(record.Hash, hash, StringComparison.Ordinal)));
        }
    }

    /// <inheritdoc />
    public ValueTask TouchAsync(Guid id, DateTimeOffset usedAt, DateTimeOffset staleBefore,
        CancellationToken cancellationToken = default) {
        lock (_gate) {
            var index = _records.FindIndex(record => record.Id == id);
            if (index >= 0 && (_records[index].LastUsedAt is null || _records[index].LastUsedAt < staleBefore))
                _records[index] = _records[index] with { LastUsedAt = usedAt };
            return ValueTask.CompletedTask;
        }
    }
}
