using System.Data.Common;
using System.Globalization;
using Elarion.Abstractions.Pipeline;

namespace Elarion.Sql;

/// <summary>
/// The EF-free SQL-tier <see cref="IUnitOfWork"/>: opens a real database transaction on the scope's shared
/// <see cref="ISqlSession"/> connection so the framework transaction and idempotency decorators commit a
/// handler's raw-SQL writes atomically — the AOT/EF-free counterpart to
/// <c>Elarion.EntityFrameworkCore.UnitOfWork.EfUnitOfWork&lt;TDbContext&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the EF unit of work there is no change tracker to flush: the SQL tier has no pending-change buffer, so
/// a handler's statements have already executed against the connection inside the transaction by the time commit
/// runs. Commit therefore only commits; rollback only rolls back.
/// </para>
/// <para>
/// The scope <b>joins</b> an already-open transaction rather than failing: when a transactional command invokes
/// another transactional command through <c>IHandlerSender</c> on the same scope, the inner
/// <see cref="BeginAsync"/> sees the ambient transaction and returns a savepoint-backed nested scope (savepoint on
/// begin, release on commit, roll-back-to-savepoint on rollback) — PostgreSQL forbids a second physical
/// transaction on one connection, and the outer scope owns the real commit.
/// </para>
/// <para>
/// Provider-awareness is best-effort: <see cref="UnitOfWorkOptions.LockTimeout"/> is applied via
/// <c>SET LOCAL lock_timeout</c> only on Npgsql connections (detected by connection type, because this package
/// takes no Npgsql dependency); other providers ignore the timeout, the closest semantics an ADO.NET provider
/// without a lock-timeout knob can offer.
/// </para>
/// </remarks>
internal sealed class SqlUnitOfWork(SqlSession session) : IUnitOfWork {
    /// <inheritdoc />
    public async ValueTask<IUnitOfWorkScope> BeginAsync(UnitOfWorkOptions options, CancellationToken ct) {
        var connection = await session.GetConnectionAsync(ct).ConfigureAwait(false);

        if (session.CurrentTransaction is { } ambient) {
            // A transaction is already open on this scope's connection (a nested transactional handler invoked
            // through IHandlerSender). Join it with a savepoint instead of opening a second physical transaction,
            // which PostgreSQL would reject.
            var savepoint = "elarion_uow_nested_" + Interlocked.Increment(ref session.NestingDepth)
                .ToString(CultureInfo.InvariantCulture);
            await ambient.SaveAsync(savepoint, ct).ConfigureAwait(false);

            string? restoreLockTimeout = null;
            if (options.LockTimeout is { } nestedLockTimeout && IsPostgres(connection)) {
                // The requested timeout must bound the nested scope's lock waits too (a nested [Idempotent]
                // command must not block unbounded on the claim row while holding the outer scope's locks).
                // SET LOCAL persists to the end of the PHYSICAL transaction, so capture the ambient value first:
                // the commit path restores it when this nested scope exits. (The rollback paths roll back to the
                // savepoint created above, which reverts the SET LOCAL automatically.)
                restoreLockTimeout = await ReadLockTimeoutAsync(connection, ambient, ct).ConfigureAwait(false);
                await ApplyLockTimeoutAsync(connection, ambient, nestedLockTimeout, ct).ConfigureAwait(false);
            }

            return new NestedScope(connection, ambient, savepoint, restoreLockTimeout);
        }

        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        session.CurrentTransaction = transaction;

        if (options.LockTimeout is { } lockTimeout && IsPostgres(connection))
            await ApplyLockTimeoutAsync(connection, transaction, lockTimeout, ct).ConfigureAwait(false);

        return new RootScope(session, transaction);
    }

    // The SQL tier takes no Npgsql dependency, so detect PostgreSQL structurally by the ADO.NET provider's
    // connection type. Non-Npgsql providers skip lock_timeout (they have no equivalent transaction-scoped knob).
    private static bool IsPostgres(DbConnection connection) {
        return connection.GetType().FullName?.StartsWith("Npgsql.", StringComparison.Ordinal) == true;
    }

    private static async Task ApplyLockTimeoutAsync(
        DbConnection connection, DbTransaction transaction, TimeSpan lockTimeout, CancellationToken ct) {
        // lock_timeout takes a value in milliseconds when no unit is given. SET LOCAL scopes it to the current
        // transaction. The value is a bounded integer we compute, not user input, so it is safe to inline.
        var milliseconds = (int)Math.Max(0, Math.Round(lockTimeout.TotalMilliseconds));
        await ExecuteNonQueryAsync(
            connection, transaction,
            "SET LOCAL lock_timeout = " + milliseconds.ToString(CultureInfo.InvariantCulture), ct)
            .ConfigureAwait(false);
    }

    private static async Task<string> ReadLockTimeoutAsync(
        DbConnection connection, DbTransaction transaction, CancellationToken ct) {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false)) {
            command.Transaction = transaction;
            command.CommandText = "SELECT current_setting('lock_timeout')";
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value as string ?? "0";
        }
    }

    private static async Task RestoreLockTimeoutAsync(
        DbConnection connection, DbTransaction transaction, string value, CancellationToken ct) {
        // set_config(..., is_local: true) reverts at the end of the transaction like SET LOCAL, but takes the
        // value as a bound parameter (the captured ambient string), so it is injection-safe.
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false)) {
            command.Transaction = transaction;
            command.CommandText = "SELECT set_config('lock_timeout', @value, true)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "value";
            parameter.Value = value;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection, DbTransaction transaction, string sql, CancellationToken ct) {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false)) {
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The root scope that owns the physical transaction. Commit commits it; dispose without an explicit commit
    /// rolls back. Either way the ambient reference is cleared so the next unit of work on the scope starts a
    /// fresh transaction.
    /// </summary>
    private sealed class RootScope(SqlSession session, DbTransaction transaction) : IUnitOfWorkScope {
        public ValueTask CommitAsync(CancellationToken ct) {
            return new ValueTask(transaction.CommitAsync(ct));
        }

        public ValueTask RollbackAsync(CancellationToken ct) {
            return new ValueTask(transaction.RollbackAsync(ct));
        }

        public ValueTask CreateSavepointAsync(string name, CancellationToken ct) {
            return new ValueTask(transaction.SaveAsync(name, ct));
        }

        public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) {
            return new ValueTask(transaction.RollbackAsync(name, ct));
        }

        public async ValueTask DisposeAsync() {
            // Clear before disposing so a failure during dispose still detaches the dead transaction from the
            // session. Disposing an uncommitted transaction rolls it back (the "dispose == rollback" contract).
            if (ReferenceEquals(session.CurrentTransaction, transaction)) session.CurrentTransaction = null;
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A nested scope that joins the ambient transaction via a savepoint. It never commits or rolls back the
    /// physical transaction (the outer root scope owns that): commit releases the savepoint, rollback discards
    /// only this nested handler's writes by rolling back to the savepoint. When the nested scope applied its own
    /// <c>lock_timeout</c>, the commit path restores the captured ambient value (SET LOCAL would otherwise persist
    /// to the end of the physical transaction); the rollback paths revert it implicitly, because rolling back to a
    /// savepoint undoes SET LOCALs issued after it.
    /// </summary>
    private sealed class NestedScope(
        DbConnection connection,
        DbTransaction transaction,
        string savepoint,
        string? restoreLockTimeout) : IUnitOfWorkScope {
        private bool _completed;

        public async ValueTask CommitAsync(CancellationToken ct) {
            await transaction.ReleaseAsync(savepoint, ct).ConfigureAwait(false);
            if (restoreLockTimeout is not null)
                // Releasing the savepoint keeps this scope's SET LOCAL alive for the rest of the physical
                // transaction — put the ambient value back so the outer scope's statements are unaffected.
                await RestoreLockTimeoutAsync(connection, transaction, restoreLockTimeout, ct).ConfigureAwait(false);

            _completed = true;
        }

        public async ValueTask RollbackAsync(CancellationToken ct) {
            await transaction.RollbackAsync(savepoint, ct).ConfigureAwait(false);
            _completed = true;
        }

        public ValueTask CreateSavepointAsync(string name, CancellationToken ct) {
            return new ValueTask(transaction.SaveAsync(name, ct));
        }

        public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) {
            return new ValueTask(transaction.RollbackAsync(name, ct));
        }

        public async ValueTask DisposeAsync() {
            if (!_completed)
                // Dispose without an explicit commit rolls back this nested unit of work only — never the ambient
                // transaction the outer scope still owns.
                await transaction.RollbackAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
