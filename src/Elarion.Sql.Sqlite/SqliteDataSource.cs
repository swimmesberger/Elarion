using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Elarion.Sql.Sqlite;

/// <summary>
/// A minimal <see cref="DbDataSource"/> over SQLite — the piece <c>Microsoft.Data.Sqlite</c> does not ship (unlike
/// Npgsql's <c>NpgsqlDataSource</c>). It lets the EF-free access tier open connections from a data source the same
/// way it does on PostgreSQL: the scoped <see cref="ISqlSession"/> pins one connection from here per request, and
/// the base class supplies the open/create plumbing over <see cref="CreateDbConnection"/>.
/// </summary>
internal sealed class SqliteDataSource(string connectionString) : DbDataSource {
    public override string ConnectionString => connectionString;

    protected override DbConnection CreateDbConnection() {
        return new SqliteConnection(connectionString);
    }
}
