using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Elarion.BulkOperations.PostgreSql;

/// <summary>Registers the PostgreSQL bulk operations provider on a context's options.</summary>
public static class PostgreSqlBulkOperationsDbContextOptionsBuilderExtensions {
    /// <summary>
    /// Enables <c>ExecuteInsertAsync</c> for this context using PostgreSQL binary <c>COPY</c>.
    /// Compose with the Npgsql provider:
    /// <c>options.UseNpgsql(...).UseElarionPostgreSqlBulkOperations()</c>.
    /// </summary>
    public static DbContextOptionsBuilder UseElarionPostgreSqlBulkOperations(this DbContextOptionsBuilder optionsBuilder) {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var extension = optionsBuilder.Options.FindExtension<PostgreSqlBulkOperationsOptionsExtension>()
            ?? new PostgreSqlBulkOperationsOptionsExtension();
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        return optionsBuilder;
    }

    /// <inheritdoc cref="UseElarionPostgreSqlBulkOperations(DbContextOptionsBuilder)"/>
    public static DbContextOptionsBuilder<TContext> UseElarionPostgreSqlBulkOperations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseElarionPostgreSqlBulkOperations((DbContextOptionsBuilder)optionsBuilder);
}
