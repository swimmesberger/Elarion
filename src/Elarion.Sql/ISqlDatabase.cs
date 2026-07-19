using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// The application's handle on <b>the</b> database the SQL tier talks to — the EF-free tier's counterpart to
/// EF Core's <c>DbContext</c>/<c>IDbContextFactory</c> pair. It is the single source of truth for which
/// <see cref="DbDataSource"/> the tier uses: the scoped <see cref="ISqlSession"/> opens its connection from it,
/// <see cref="SqlDatabaseExtensions.OpenSessionAsync"/> opens an owning one-shot session from it (the
/// singleton-eligible path), and a provider package's registration (<c>AddElarionPostgreSql</c>,
/// <c>AddElarionSqlite</c>) chooses its implementation.
/// </summary>
/// <remarks>
/// The seam keeps the tier from assuming a single ambient data source: a host registers a scoped implementation
/// to route per request (read the tenant from <c>ICurrentUser</c> and return that tenant's or a replica's pooled
/// data source), while the common single-database host uses the default over one <see cref="DbDataSource"/>.
/// This mirrors how the migration runner takes an explicit data source rather than resolving a global one — the
/// database is named at the seam, not assumed.
/// </remarks>
public interface ISqlDatabase {
    /// <summary>Returns the data source the current scope should open connections from.</summary>
    DbDataSource GetDataSource();
}

/// <summary>
/// The default <see cref="ISqlDatabase"/>: always returns the one data source it was built with
/// (a container-registered <see cref="DbDataSource"/>, or the one a provider registration such as
/// <c>AddElarionPostgreSql</c> created). The single-database happy path.
/// </summary>
internal sealed class DataSourceSqlDatabase(DbDataSource dataSource) : ISqlDatabase {
    public DbDataSource GetDataSource() {
        return dataSource;
    }
}
