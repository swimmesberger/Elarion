using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Transactions;
using Elarion.Abstractions.Coordination;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Elarion.EntityFrameworkCore.UnitOfWork;

/// <summary>How strongly <see cref="DbContextLockExtensions.LockRowAsync{TEntity}(DbContext, object, RowLockMode, CancellationToken)"/> locks a row.</summary>
/// <remarks>
/// The PostgreSQL row-lock strengths, strongest first. Pick deliberately: the difference between the first two is
/// whether a concurrent insert of a row <i>referencing</i> the locked one (a foreign key) waits.
/// </remarks>
public enum RowLockMode {
    /// <summary>
    /// <c>FOR UPDATE</c>: excludes every other lock on the row, including the <c>FOR KEY SHARE</c> PostgreSQL takes
    /// while another transaction inserts a row referencing it — for a lock that must make such an insert wait.
    /// </summary>
    Update,

    /// <summary>
    /// <c>FOR NO KEY UPDATE</c>: serializes writers of the row itself, but a concurrent insert of a referencing row
    /// does not wait.
    /// </summary>
    NoKeyUpdate,

    /// <summary><c>FOR SHARE</c>: concurrent readers that also share-lock proceed; writers wait.</summary>
    Share,

    /// <summary><c>FOR KEY SHARE</c>: only a change of the row's key (or its deletion) waits.</summary>
    KeyShare
}

/// <summary>How <see cref="DbContextLockExtensions.LockRowAsync{TEntity}(DbContext, object, RowLockOptions, CancellationToken)"/> finds and locks a row.</summary>
public sealed record RowLockOptions {
    /// <summary>The lock strength. Defaults to <see cref="RowLockMode.Update"/>.</summary>
    public RowLockMode Mode { get; init; } = RowLockMode.Update;

    /// <summary>
    /// Look the row up past the entity's query filters, as <c>IgnoreQueryFilters()</c> does: an archived or soft-deleted
    /// row a writer deliberately changes is then found and locked. Every filter is skipped, tenant scoping included, so
    /// use it only for an id the handler has already authorized.
    /// </summary>
    public bool IgnoreQueryFilters { get; init; }
}

/// <summary>
/// Transaction-scoped PostgreSQL locks on a <see cref="DbContext"/>: one row of an entity
/// (<see cref="LockRowAsync{TEntity}(DbContext, object, RowLockMode, CancellationToken)"/>) or an advisory key (<see cref="AdvisoryLockAsync"/>,
/// <see cref="TryAdvisoryLockAsync"/>). Both are held until the current transaction commits or rolls back — a
/// command's pipeline transaction, or one the caller opened — and never across it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default to the database's own guarantees</b> — constraints, optimistic concurrency, <c>ON CONFLICT</c>. Reach for
/// a lock when a rule spans rows a constraint cannot express (no overlapping periods across a whole set, a
/// read-then-write that must not interleave), and keep it on the narrowest key.
/// </para>
/// <para>
/// A lock outside a transaction would be released at the end of its own statement, so both methods throw when the
/// context has no transaction (an EF transaction or an ambient <see cref="System.Transactions.Transaction"/>, which
/// counts because the connection enlists in it — a connection string with <c>Enlist=false</c> opts out). Every
/// provider other than PostgreSQL is refused rather than silently not locking. Code that tests locking runs against
/// PostgreSQL (a Testcontainers instance). A test context on another provider whose tests only pass through a lock opts
/// out explicitly with <see cref="UnenforcedLocksDbContextOptionsExtensions.UseUnenforcedElarionLocks(DbContextOptionsBuilder)"/>.
/// </para>
/// <para>
/// Advisory keys follow <see cref="AdvisoryLockKey"/>: <c>hashtextextended('scope:key', 0)</c>, with the
/// <c>elarion.</c> scopes reserved for the framework.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Periods of one lease unit must not overlap: serialize every writer of the unit's periods.
/// if (!await db.LockRowAsync&lt;LeaseUnit&gt;(request.UnitId, RowLockMode.NoKeyUpdate, ct))
///     return AppError.NotFound("No such unit.");
///
/// // No row to lock (the account may not exist yet): lock the key instead.
/// await db.AdvisoryLockAsync("billing-account", request.AccountNumber, ct);
/// if (!await db.TryAdvisoryLockAsync("statement-run", request.PeriodKey, ct))
///     return AppError.Conflict("A statement run for this period is already in progress.");
/// </code>
/// </example>
public static class DbContextLockExtensions {
    // Compared by name: the package deliberately takes no Npgsql reference (EF Relational only).
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    // The row-lock target per model and entity type, built from the model once. Weak keys, so a model (and its
    // service graph) lives exactly as long as without the cache.
    private static readonly ConditionalWeakTable<IModel, Dictionary<Type, RowLockTarget>> RowLockTargets = new();

    private static readonly MethodInfo IsVisibleMethod = typeof(DbContextLockExtensions)
        .GetMethod(nameof(IsVisibleAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Locks the row of <typeparamref name="TEntity"/> whose primary key is <paramref name="id"/>
    /// (<c>SELECT … FOR UPDATE</c> or the chosen <paramref name="mode"/>), waiting for a conflicting holder. Table
    /// and key column come from the model; the entity needs a single-column primary key mapped to a table.
    /// </summary>
    /// <remarks>
    /// The row is first looked up through the entity's own set, so its query filters (tenant scoping, soft delete) and
    /// a hierarchy's discriminator apply: a row the context cannot see is reported missing and never locked, so an id
    /// from a request cannot probe or stall another tenant's rows. The key is converted to its provider value, so a
    /// strongly typed key with a value converter works. To lock a filtered row on purpose, use the
    /// <see cref="RowLockOptions"/> overload with <see cref="RowLockOptions.IgnoreQueryFilters"/>.
    /// </remarks>
    /// <returns><see langword="true"/> when the row is visible and now locked; <see langword="false"/> when no visible row has that key.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not of the key's type.</exception>
    /// <exception cref="InvalidOperationException">The context has no transaction, or the entity has no single-column table key.</exception>
    /// <exception cref="NotSupportedException">The provider is not PostgreSQL.</exception>
    public static Task<bool> LockRowAsync<TEntity>(
        this DbContext db, object id, RowLockMode mode = RowLockMode.Update, CancellationToken ct = default)
        where TEntity : class {
        return db.LockRowAsync<TEntity>(id, new RowLockOptions { Mode = mode }, ct);
    }

    /// <summary>
    /// Locks the row of <typeparamref name="TEntity"/> whose primary key is <paramref name="id"/> as
    /// <paramref name="options"/> says — its <see cref="RowLockOptions.Mode"/>, and whether the lookup skips the entity's
    /// query filters (<see cref="RowLockOptions.IgnoreQueryFilters"/>).
    /// </summary>
    /// <example>
    /// <code>
    /// // A portal-link writer deliberately updates archived tenants too, so the lock must find them.
    /// if (!await db.LockRowAsync&lt;Tenant&gt;(request.TenantId,
    ///         new RowLockOptions { Mode = RowLockMode.NoKeyUpdate, IgnoreQueryFilters = true }, ct))
    ///     return AppError.NotFound("No such tenant.");
    /// </code>
    /// </example>
    /// <returns><see langword="true"/> when the row is found and now locked; <see langword="false"/> when no row has that key.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not of the key's type.</exception>
    /// <exception cref="InvalidOperationException">The context has no transaction, or the entity has no single-column table key.</exception>
    /// <exception cref="NotSupportedException">The provider is not PostgreSQL.</exception>
    public static async Task<bool> LockRowAsync<TEntity>(
        this DbContext db, object id, RowLockOptions options, CancellationToken ct = default)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(options);
        var enforced = EnsureLockable(db);

        var target = RowLockTargetOf<TEntity>(db);
        var keyType = Nullable.GetUnderlyingType(target.Key.ClrType) ?? target.Key.ClrType;
        if (!keyType.IsInstanceOfType(id))
            throw new ArgumentException(
                $"{typeof(TEntity).Name} is keyed by {keyType.Name}; the id is a {id.GetType().Name}.", nameof(id));

        var visible = await target.IsVisible(db, target.Key.Name, id, options.IgnoreQueryFilters, ct)
            .ConfigureAwait(false);
        if (!visible || !enforced) return visible;

        var sql = (target.Statement ?? throw new InvalidOperationException(
            $"{typeof(TEntity).Name} is not mapped to a table.")) + options.Mode switch {
            RowLockMode.Update => " FOR UPDATE",
            RowLockMode.NoKeyUpdate => " FOR NO KEY UPDATE",
            RowLockMode.Share => " FOR SHARE",
            RowLockMode.KeyShare => " FOR KEY SHARE",
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Mode, null)
        };
        var providerValue = target.Key.GetRelationalTypeMapping().Converter?.ConvertToProvider(id) ?? id;
        var rows = await db.Database.SqlQueryRaw<int>(sql, providerValue).ToListAsync(ct).ConfigureAwait(false);
        return rows.Count > 0;
    }

    /// <summary>
    /// Takes the transaction-scoped advisory lock on <c>(<paramref name="scope"/>, <paramref name="key"/>)</c>
    /// (<c>pg_advisory_xact_lock</c>), waiting for a holder in another transaction. The scope names what is
    /// serialized (<c>"billing-account"</c>) and the key which one (usually an id).
    /// </summary>
    /// <exception cref="ArgumentException">The scope is empty, contains <c>:</c>, or starts with the reserved <c>elarion.</c>.</exception>
    /// <exception cref="InvalidOperationException">The context has no transaction.</exception>
    /// <exception cref="NotSupportedException">The provider is not PostgreSQL.</exception>
    public static async Task AdvisoryLockAsync(this DbContext db, string scope, string key, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(db);
        var text = AdvisoryLockKey.Text(scope, key);
        if (!EnsureLockable(db)) return;

        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))", [text], ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Tries the advisory lock of <see cref="AdvisoryLockAsync"/> without waiting (<c>pg_try_advisory_xact_lock</c>):
    /// <see langword="true"/> when this transaction now holds it (or already did), <see langword="false"/> when another
    /// transaction does.
    /// </summary>
    /// <exception cref="ArgumentException">The scope is empty, contains <c>:</c>, or starts with the reserved <c>elarion.</c>.</exception>
    /// <exception cref="InvalidOperationException">The context has no transaction.</exception>
    /// <exception cref="NotSupportedException">The provider is not PostgreSQL.</exception>
    public static async Task<bool> TryAdvisoryLockAsync(
        this DbContext db, string scope, string key, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(db);
        var text = AdvisoryLockKey.Text(scope, key);
        if (!EnsureLockable(db)) return true;

        var acquired = await db.Database
            .SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(hashtextextended({0}, 0)) AS \"Value\"", text)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return acquired is [true];
    }

    // Throws for the two ways a lock would silently protect nothing: a provider without PostgreSQL's locks, and a
    // missing transaction. Returns false for a test context opted out with UseUnenforcedElarionLocks: lock nothing.
    private static bool EnsureLockable(DbContext db) {
        var postgres = string.Equals(db.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal);
        if (UnenforcedLocksOptionsExtension.IsOn(db)) {
            if (postgres)
                throw new InvalidOperationException(
                    "UseUnenforcedElarionLocks is for test contexts on a provider without PostgreSQL's locks; a PostgreSQL " +
                    "context always takes them. Remove it from this context's options.");
            return false;
        }

        if (!postgres)
            throw new NotSupportedException(
                $"Row and advisory locks need PostgreSQL; the provider is '{db.Database.ProviderName}'. A test context on " +
                "another provider that does not exercise locking can opt out with UseUnenforcedElarionLocks().");

        // An ambient System.Transactions transaction counts only because the connection enlists in it by default; a
        // connection string with Enlist=false would autocommit each statement — documented, not detectable here.
        if (db.Database.CurrentTransaction is null && Transaction.Current is null)
            throw new InvalidOperationException(
                "A transaction-scoped lock needs a transaction: outside one, PostgreSQL releases it when the statement ends. " +
                "Lock inside a command (the pipeline's unit of work) or a transaction you opened.");
        return true;
    }

    private static RowLockTarget RowLockTargetOf<TEntity>(DbContext db) where TEntity : class {
        var targets = RowLockTargets.GetValue(db.Model, static _ => new Dictionary<Type, RowLockTarget>());
        lock (targets) {
            if (targets.TryGetValue(typeof(TEntity), out var cached)) return cached;

            var entity = db.Model.FindEntityType(typeof(TEntity))
                         ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not part of the model.");
            var keyProperties = entity.FindPrimaryKey()?.Properties;
            if (keyProperties is not { Count: 1 })
                throw new InvalidOperationException(
                    $"{typeof(TEntity).Name} has no single-column primary key to lock a row by.");

            var key = keyProperties[0];
            var target = new RowLockTarget(
                StatementOf(db, entity, key),
                key,
                IsVisibleMethod.MakeGenericMethod(typeof(TEntity), key.ClrType)
                    .CreateDelegate<Func<DbContext, string, object, bool, CancellationToken, Task<bool>>>());
            targets[typeof(TEntity)] = target;
            return target;
        }
    }

    // The lock statement, for a relational model whose entity maps to a table; null otherwise (an unenforced test
    // context on EF InMemory looks rows up but never runs it).
    private static string? StatementOf(DbContext db, IEntityType entity, IProperty key) {
        if (!db.Database.IsRelational() || entity.GetTableName() is not { } tableName) return null;

        var table = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
        var sql = db.GetService<ISqlGenerationHelper>();
        var column = sql.DelimitIdentifier(key.GetColumnName(table)
                                           ?? throw new InvalidOperationException(
                                               $"{entity.ClrType.Name}'s key has no column."));
        return $"SELECT 1 AS \"Value\" FROM {sql.DelimitIdentifier(table.Name, table.Schema)} WHERE {column} = {{0}}";
    }

    // The row through the entity's own set: query filters (unless ignored on purpose) and the discriminator apply, and
    // the id is a parameter.
    private static Task<bool> IsVisibleAsync<TEntity, TKey>(
        DbContext db, string keyName, object id, bool ignoreQueryFilters, CancellationToken ct) where TEntity : class {
        var row = Expression.Parameter(typeof(TEntity), "row");
        var keyAccess = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(TKey)], row, Expression.Constant(keyName));
        var value = new StrongBox<TKey>((TKey)id);
        var matches = Expression.Lambda<Func<TEntity, bool>>(
            Expression.Equal(keyAccess, Expression.Field(Expression.Constant(value), nameof(StrongBox<TKey>.Value))),
            row);
        IQueryable<TEntity> rows = db.Set<TEntity>();
        if (ignoreQueryFilters) rows = rows.IgnoreQueryFilters();
        return rows.AnyAsync(matches, ct);
    }

    private sealed record RowLockTarget(
        string? Statement,
        IProperty Key,
        Func<DbContext, string, object, bool, CancellationToken, Task<bool>> IsVisible);
}
