using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.EntityFrameworkCore.UnitOfWork;

/// <summary>Opting a test context out of lock enforcement.</summary>
public static class UnenforcedLocksDbContextOptionsExtensions {
    /// <summary>
    /// Makes <see cref="DbContextLockExtensions.LockRowAsync{TEntity}(DbContext, object, RowLockMode, CancellationToken)"/>,
    /// <see cref="DbContextLockExtensions.AdvisoryLockAsync"/> and <see cref="DbContextLockExtensions.TryAdvisoryLockAsync"/>
    /// lock nothing on this context, for <b>tests on a provider without PostgreSQL's locks</b> (EF InMemory, SQLite) that
    /// do not exercise locking. The provider and transaction checks are skipped. Everything else still runs: keys and
    /// scopes are validated, and <c>LockRowAsync</c> still answers whether a visible row has the key, so a handler's
    /// not-found path behaves as in production.
    /// </summary>
    /// <remarks>
    /// It is an explicit opt-in on the test context's own options, never a fallback: a context without it still fails
    /// closed on another provider. A PostgreSQL context that carries it throws at its first lock, so a production
    /// context cannot be left unenforced by a shared options helper. Locking itself is tested on PostgreSQL.
    /// </remarks>
    /// <example>
    /// <code>
    /// var options = new DbContextOptionsBuilder&lt;AppDbContext&gt;()
    ///     .UseInMemoryDatabase(Guid.CreateVersion7().ToString())
    ///     .UseUnenforcedElarionLocks()
    ///     .Options;
    /// </code>
    /// </example>
    public static DbContextOptionsBuilder UseUnenforcedElarionLocks(this DbContextOptionsBuilder optionsBuilder) {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(new UnenforcedLocksOptionsExtension());
        return optionsBuilder;
    }

    /// <inheritdoc cref="UseUnenforcedElarionLocks(DbContextOptionsBuilder)"/>
    public static DbContextOptionsBuilder<TContext> UseUnenforcedElarionLocks<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext {
        return (DbContextOptionsBuilder<TContext>)((DbContextOptionsBuilder)optionsBuilder).UseUnenforcedElarionLocks();
    }
}

/// <summary>Marks a context's options as opted out of lock enforcement; carries no state.</summary>
/// <remarks>
/// It contributes nothing to the service-provider or model cache keys: it changes no mapping and no provider service,
/// only how the lock helpers treat the context.
/// </remarks>
internal sealed class UnenforcedLocksOptionsExtension : IDbContextOptionsExtension {
    private DbContextOptionsExtensionInfo? _info;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) {
    }

    public void Validate(IDbContextOptions options) {
    }

    internal static bool IsOn(DbContext db) {
        return db.GetService<IDbContextOptions>().FindExtension<UnenforcedLocksOptionsExtension>() is not null;
    }

    private sealed class ExtensionInfo(UnenforcedLocksOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension) {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using unenforced Elarion locks ";

        public override int GetServiceProviderHashCode() {
            return 0;
        }

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) {
            return other is ExtensionInfo;
        }

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) {
            debugInfo["Elarion:UnenforcedLocks"] = "1";
        }
    }
}
