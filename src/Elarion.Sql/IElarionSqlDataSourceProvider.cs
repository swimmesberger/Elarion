using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// Supplies the <see cref="DbDataSource"/> a scoped <see cref="ISqlSession"/> opens its connection from. The seam
/// keeps the SQL tier from assuming a single ambient data source in the container: a host implements it to route
/// the connection per scope — a tenant's database, a read replica, a keyed source — while the common
/// single-database host uses the default that wraps one registered <see cref="DbDataSource"/>.
/// </summary>
/// <remarks>
/// Register a scoped implementation to route per request (read the tenant from <c>ICurrentUser</c> and return that
/// tenant's pooled data source); the session resolves the provider once per scope. This mirrors how the migration
/// runner takes an explicit data source rather than resolving a global one — the data source is named at the seam,
/// not assumed.
/// </remarks>
public interface IElarionSqlDataSourceProvider {
    /// <summary>Returns the data source the current scope's session should open its connection from.</summary>
    DbDataSource GetDataSource();
}

/// <summary>
/// The default <see cref="IElarionSqlDataSourceProvider"/>: always returns the one data source it was built with
/// (a container-registered <see cref="DbDataSource"/>, or one named through a factory overload of
/// <c>AddElarionSqlSession</c>/<c>AddElarionSqlUnitOfWork</c>). The single-database happy path.
/// </summary>
internal sealed class SingletonSqlDataSourceProvider(DbDataSource dataSource) : IElarionSqlDataSourceProvider {
    public DbDataSource GetDataSource() {
        return dataSource;
    }
}
