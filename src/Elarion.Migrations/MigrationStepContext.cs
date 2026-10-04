using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Migrations;

/// <summary>
/// What one <see cref="MigrationStep"/> executes against (ADR-0081): a fresh dependency-injection scope and
/// the plan's own database connection — the one that holds the cross-instance migration lock — with the
/// step's transaction when it has one.
/// </summary>
public sealed class MigrationStepContext {
    private readonly IMigrationSession _session;

    internal MigrationStepContext(MigrationStep step, IServiceProvider services, IMigrationSession session,
        DbTransaction? transaction) {
        Step = step;
        Services = services;
        Transaction = transaction;
        _session = session;
    }

    /// <summary>The step being executed.</summary>
    public MigrationStep Step { get; }

    /// <summary>
    /// A fresh scope per step, disposed afterwards: resolve a <c>DbContext</c>, an <c>ISqlSession</c> or any
    /// scoped service here. Services resolved from it use their <em>own</em> connections, outside
    /// <see cref="Transaction"/> — their writes are not rolled back when the step fails, so a conversion that
    /// goes through them must be idempotent. A runner constructed without a service provider exposes an empty
    /// one.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// The plan's open connection. Commands on it join <see cref="Transaction"/> when the step is
    /// transactional; an EF Core context can adopt both through <c>Database.SetDbConnection</c> and
    /// <c>Database.UseTransaction</c> to make its writes atomic with the history record. The runner owns and
    /// disposes the connection; never close it.
    /// </summary>
    public DbConnection Connection => _session.Connection;

    /// <summary>
    /// The transaction whose commit records the step, or <see langword="null"/> when the step does not use one
    /// (<see cref="MigrationStep.UseTransaction"/> is <see langword="false"/>, or during
    /// <see cref="MigrationStep.IsAlreadySatisfiedAsync"/>). The runner commits it; never commit or roll it back.
    /// </summary>
    public DbTransaction? Transaction { get; }

    /// <summary>
    /// Executes <paramref name="sql"/> on <see cref="Connection"/> in <see cref="Transaction"/> using the
    /// provider's own execution rules — the same path SQL script steps take (outside a transaction the
    /// PostgreSQL provider runs the statements one by one so <c>CREATE INDEX CONCURRENTLY</c> works).
    /// </summary>
    /// <param name="sql">The SQL text; interpolate nothing untrusted into it.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    public Task ExecuteSqlAsync(string sql, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(sql);
        return _session.ExecuteSqlAsync(sql, Transaction, cancellationToken);
    }

    /// <summary>Resolves a required service from <see cref="Services"/>.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    public T GetRequiredService<T>() where T : notnull {
        return Services.GetRequiredService<T>();
    }
}

/// <summary>The scope-less service provider of a runner built without one: resolves nothing.</summary>
internal sealed class EmptyServiceProvider : IServiceProvider {
    public static readonly EmptyServiceProvider Instance = new();

    public object? GetService(Type serviceType) {
        return null;
    }
}
