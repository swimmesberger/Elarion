using Microsoft.Extensions.Logging;

namespace Elarion.Settings.EntityFrameworkCore;

/// <summary>
/// Scoped buffer that commit-gates in-process settings change notifications for writes made inside a
/// caller-owned EF Core transaction. A write with no ambient transaction is announced immediately (it is already
/// durable); a write inside a transaction is <see cref="Defer">deferred</see> and announced by the
/// <see cref="SettingsChangeDispatchTransactionInterceptor"/> only after the transaction commits — and dropped on
/// rollback — so a watcher never reloads a value a rollback discards.
/// </summary>
/// <remarks>
/// This is the in-process analogue of the PostgreSQL notifier's commit-gating (which rides the database's own
/// transactional <c>NOTIFY</c>), and it mirrors the in-memory integration-event <c>EventDispatchScope</c>: an
/// internal implementation detail driven from the DbContext transaction lifecycle, not a public seam.
/// <para>
/// The buffer is <b>savepoint-aware</b>: <see cref="PushSavepoint"/> records the buffer high-water mark when a
/// savepoint is created, <see cref="RollbackToSavepoint"/> truncates the changes buffered after it, and
/// <see cref="ReleaseSavepoint"/> pops the mark. This keeps a partial rollback — e.g. a nested command rolling
/// back to its savepoint while the outer transaction still commits (the idempotency-decorator shape) — from
/// announcing a change that was undone. The EF Core transaction-interceptor callbacks carry no savepoint name, so
/// the marks form a LIFO stack, which matches how nested savepoints release/roll back.
/// </para>
/// <para>
/// Announcing runs the in-process watch-token callbacks synchronously on the committing thread, so a throwing
/// watcher is caught and logged rather than surfaced out of the caller's commit — a settings watcher must never
/// fail the command whose transaction it observed.
/// </para>
/// </remarks>
internal sealed class SettingsChangeDispatchScope(
    ISettingsChangePublisher publisher,
    ILogger<SettingsChangeDispatchScope> logger) {
    private readonly List<(SettingsScope Scope, string Key)> _buffer = [];

    // Buffer high-water marks, one per active savepoint, in creation (stack) order. The interceptor callbacks carry
    // no savepoint name, so rollback/release target the top of the stack — correct for LIFO nesting.
    private readonly Stack<int> _savepointMarks = new();

    /// <summary>Announces a change immediately — the write is already durable (no ambient transaction).</summary>
    public void PublishNow(SettingsScope scope, string key) => Announce(scope, key);

    /// <summary>Buffers a change made inside a caller-owned transaction; <see cref="Flush"/> announces it after commit.</summary>
    public void Defer(SettingsScope scope, string key) => _buffer.Add((scope, key));

    /// <summary>Records the current buffer size as the high-water mark for a newly created savepoint.</summary>
    public void PushSavepoint() => _savepointMarks.Push(_buffer.Count);

    /// <summary>
    /// Truncates the buffer back to the most recent savepoint mark, dropping every change buffered after that
    /// savepoint. The mark is kept: SQL <c>ROLLBACK TO SAVEPOINT</c> leaves the savepoint intact, so it may be
    /// rolled back to again or released later.
    /// </summary>
    public void RollbackToSavepoint() {
        if (_savepointMarks.Count == 0) {
            return;
        }

        var mark = _savepointMarks.Peek();
        if (mark < _buffer.Count) {
            _buffer.RemoveRange(mark, _buffer.Count - mark);
        }
    }

    /// <summary>Pops the most recent savepoint mark without touching the buffer (SQL <c>RELEASE SAVEPOINT</c>).</summary>
    public void ReleaseSavepoint() {
        if (_savepointMarks.Count > 0) {
            _savepointMarks.Pop();
        }
    }

    /// <summary>Announces every buffered change after the caller's transaction commits.</summary>
    public void Flush() {
        _savepointMarks.Clear();
        if (_buffer.Count == 0) {
            return;
        }

        foreach (var (scope, key) in _buffer) {
            Announce(scope, key);
        }

        _buffer.Clear();
    }

    /// <summary>Drops every buffered change after the caller's transaction rolls back.</summary>
    public void Discard() {
        _buffer.Clear();
        _savepointMarks.Clear();
    }

    private void Announce(SettingsScope scope, string key) {
        try {
            publisher.Publish(scope, key);
        }
        catch (Exception ex) {
            // Publishing fires in-process watch-token callbacks synchronously; a throwing watcher must not
            // propagate out of the store write or the committing transaction.
            logger.LogDebug(
                ex, "A settings change watcher threw while being notified of '{Key}' in scope '{Scope}'.", key, scope.Kind);
        }
    }
}
