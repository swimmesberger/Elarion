using System.Collections.Concurrent;
using Elarion.Abstractions.Idempotency;
using Microsoft.Extensions.Logging;

namespace Elarion.Idempotency;

/// <summary>
/// A best-effort, single-instance <see cref="IIdempotencyStore"/> for dev/test — the idempotency analog of the
/// in-memory event bus/scheduler. A <see cref="ConcurrentDictionary{TKey, TValue}"/> is the dedup gate:
/// <see cref="ConcurrentDictionary{TKey, TValue}.TryAdd"/> is the atomic claim (the in-memory analog of a unique
/// constraint), and a per-entry signal lets <see cref="IdempotencyConflictBehavior.WaitThenReplay"/> callers
/// wait for the winner. Not durable and not cross-instance — use the EF Core store for the real guarantees.
/// </summary>
internal sealed class InMemoryIdempotencyStore(
    TimeProvider timeProvider,
    ILogger<InMemoryIdempotencyStore>? logger = null) : IIdempotencyStore {
    private readonly ConcurrentDictionary<IdempotencyStoreKey, Entry> _entries = new();
    private int _warned;

    /// <inheritdoc />
    public async ValueTask<IdempotencyBeginResult> TryBeginAsync(
        IdempotencyStoreKey key,
        string fingerprint,
        IdempotencyConflictBehavior conflictBehavior,
        CancellationToken ct) {
        WarnOnce();
        while (true) {
            ct.ThrowIfCancellationRequested();

            if (_entries.TryAdd(key, new Entry(fingerprint))) {
                return IdempotencyBeginResult.Began();
            }

            if (!_entries.TryGetValue(key, out var existing)) {
                // Removed between the failed TryAdd and this read; retry the claim.
                continue;
            }

            if (existing.Completed) {
                if (existing.ExpiresOnUtc < timeProvider.GetUtcNow()) {
                    // Expired: drop it and treat the key as new.
                    _entries.TryRemove(new KeyValuePair<IdempotencyStoreKey, Entry>(key, existing));
                    continue;
                }

                if (fingerprint.Length > 0 && !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)) {
                    return IdempotencyBeginResult.FingerprintMismatch();
                }

                return IdempotencyBeginResult.Replay(existing.Payload!, existing.IsFailure);
            }

            // A pending (in-flight) claim exists.
            if (conflictBehavior == IdempotencyConflictBehavior.Conflict) {
                return IdempotencyBeginResult.InProgress();
            }

            // WaitThenReplay: block until the winner completes or abandons, then re-evaluate.
            await existing.Signal.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(
        IdempotencyStoreKey key,
        string payload,
        bool isFailure,
        TimeSpan retention,
        CancellationToken ct) {
        if (_entries.TryGetValue(key, out var entry)) {
            entry.Complete(payload, isFailure, timeProvider.GetUtcNow() + retention);
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask AbandonAsync(IdempotencyStoreKey key, CancellationToken ct) {
        if (_entries.TryRemove(key, out var entry)) {
            // Wake any WaitThenReplay waiters so they re-attempt the claim.
            entry.Signal.TrySetResult();
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeCompletedAsync(DateTimeOffset olderThanUtc, CancellationToken ct) {
        var removed = 0;
        foreach (var pair in _entries) {
            if (pair.Value is { Completed: true } entry && entry.ExpiresOnUtc < olderThanUtc &&
                _entries.TryRemove(pair)) {
                removed++;
            }
        }

        return new ValueTask<int>(removed);
    }

    private void WarnOnce() {
        if (logger is null || Interlocked.Exchange(ref _warned, 1) != 0) {
            return;
        }

        logger.LogWarning(
            "The in-memory IIdempotencyStore is in use: it is single-process and non-durable, so on a multi-node " +
            "deployment two nodes can both claim the same key and both execute. It is for single-process dev/test " +
            "only. Call AddElarionIdempotencyEntityFrameworkCore<TDbContext>() from " +
            "Elarion.Idempotency.EntityFrameworkCore for the durable, cross-node guarantee in production.");
    }

    private sealed class Entry(string fingerprint) {
        private volatile bool _completed;

        public string Fingerprint { get; } = fingerprint;
        public string? Payload { get; private set; }
        public bool IsFailure { get; private set; }
        public DateTimeOffset ExpiresOnUtc { get; private set; }
        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Completed => _completed;

        public void Complete(string payload, bool isFailure, DateTimeOffset expiresOnUtc) {
            Payload = payload;
            IsFailure = isFailure;
            ExpiresOnUtc = expiresOnUtc;
            _completed = true; // volatile write publishes the fields above to observers of Completed == true
            Signal.TrySetResult();
        }
    }
}
