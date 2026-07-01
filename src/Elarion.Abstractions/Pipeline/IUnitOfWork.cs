namespace Elarion.Abstractions.Pipeline;

/// <summary>
/// Options for opening a unit-of-work scope via <see cref="IUnitOfWork.BeginAsync"/>.
/// </summary>
public readonly record struct UnitOfWorkOptions {
    /// <summary>
    /// Optional maximum time to wait when acquiring a database lock inside the scope. When set, a backend
    /// that supports it (PostgreSQL <c>lock_timeout</c>) aborts a blocked statement instead of waiting
    /// indefinitely — this is how idempotency turns a concurrent in-flight duplicate into a fast 409 rather
    /// than a blocking wait. Backends without lock-timeout support ignore it.
    /// </summary>
    public TimeSpan? LockTimeout { get; init; }

    /// <summary>The default options (no lock timeout).</summary>
    public static UnitOfWorkOptions Default => default;
}

/// <summary>
/// A transport- and provider-neutral unit-of-work boundary. Implementations open a backend transaction so a
/// decorator (the framework <see cref="TransactionDecorator{TRequest, TResponse}"/> or the idempotency
/// decorator) can wrap a handler in one atomic commit/rollback, without depending on EF Core directly.
/// </summary>
/// <remarks>
/// The heavy EF Core implementation lives in the opt-in <c>Elarion.EntityFrameworkCore.UnitOfWork</c> package
/// (mirroring the caching/resilience seam-plus-sibling split); core ships a no-op in-memory implementation for
/// dev/test.
/// </remarks>
public interface IUnitOfWork {
    /// <summary>Opens a new unit-of-work scope.</summary>
    ValueTask<IUnitOfWorkScope> BeginAsync(UnitOfWorkOptions options, CancellationToken ct);
}

/// <summary>
/// An open unit-of-work scope. Dispose without an explicit commit rolls back.
/// </summary>
public interface IUnitOfWorkScope : IAsyncDisposable {
    /// <summary>Commits all work done in the scope.</summary>
    ValueTask CommitAsync(CancellationToken ct);

    /// <summary>Rolls back all work done in the scope.</summary>
    ValueTask RollbackAsync(CancellationToken ct);

    /// <summary>
    /// Establishes a savepoint the scope can later roll back to. Used by the opt-in
    /// idempotency "store definitive failures" path to discard the handler's business writes while keeping
    /// the idempotency-key row (inserted before the savepoint). Backends without savepoint support may no-op.
    /// </summary>
    ValueTask CreateSavepointAsync(string name, CancellationToken ct);

    /// <summary>Rolls the scope back to a previously established <paramref name="name"/> savepoint.</summary>
    ValueTask RollbackToSavepointAsync(string name, CancellationToken ct);
}
