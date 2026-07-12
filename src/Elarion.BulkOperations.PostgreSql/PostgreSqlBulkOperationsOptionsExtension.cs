using Elarion.EntityFrameworkCore.BulkOperations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.BulkOperations.PostgreSql;

/// <summary>
/// The <see cref="IDbContextOptionsExtension"/> behind <c>UseElarionPostgreSqlBulkOperations()</c>:
/// ships <see cref="PostgreSqlBulkInsertProvider"/> into the EF internal service provider, the same
/// way EF provider plugins register their services.
/// </summary>
internal sealed class PostgreSqlBulkOperationsOptionsExtension : IDbContextOptionsExtension {
    private DbContextOptionsExtensionInfo? _info;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) =>
        services.TryAddSingleton<IBulkInsertProvider, PostgreSqlBulkInsertProvider>();

    // Provider mismatch (a non-Npgsql context) surfaces at execution time with a targeted message;
    // at validation time the relational provider extension may not be configured yet.
    public void Validate(IDbContextOptions options) {
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension) {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using Elarion PostgreSQL bulk operations ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["Elarion.BulkOperations.PostgreSql"] = "1";
    }
}
