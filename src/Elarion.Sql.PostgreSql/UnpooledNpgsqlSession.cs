using Npgsql;

namespace Elarion.Sql.PostgreSql;

/// <summary>
/// One open, unpooled PostgreSQL connection that owns a private <see cref="NpgsqlDataSource"/> — a real server
/// session that ends when this object is disposed, without leaving anything behind in Npgsql's process-wide
/// pool registry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>new NpgsqlConnection(connectionString)</c>.</b> The connection-string constructor of
/// <see cref="NpgsqlConnection"/> resolves its data source through Npgsql's static pool manager, which registers
/// one <see cref="NpgsqlDataSource"/> per distinct connection string for the life of the process — there is no
/// public API to remove it again. <c>Pooling=false</c> does not opt out of the registry (it adds the
/// unpooled source too), and <c>NpgsqlConnection.ClearPool(new NpgsqlConnection(cs))</c> <em>creates</em> a pool
/// just to clear it. Each registered source also keeps a metrics reporter in a static list until disposed, so
/// none of them is ever collected. With the one connection string of a production app this is invisible; with a
/// database per test, per tenant probe, or per short-lived container it grows without bound.
/// </para>
/// <para>
/// This type builds its own data source with <c>Pooling=false</c> instead, opens a single connection from it,
/// and disposes both together. The static registry is never touched, and closing the connection closes the
/// physical connection, so everything scoped to the server session — session-level advisory locks,
/// <c>LISTEN</c> registrations, temporary tables, <c>SET</c> values — ends with it.
/// </para>
/// <para>
/// <b>Use it for</b> session-scoped work that must not share a pooled connector: a session-level
/// <c>pg_advisory_lock</c> (a pooled connector returned with the lock still held blocks every later locker),
/// <c>LISTEN</c>, statements that must run first and alone on a fresh session, and any work against a database
/// that only lives briefly — a per-test database, a template you create and drop, an admin connection to
/// <c>postgres</c> to run <c>CREATE</c>/<c>DROP DATABASE</c>.
/// </para>
/// <para>
/// <b>Do not use it for</b> the application's normal traffic. That belongs on the one long-lived data source the
/// application registers (<c>AddElarionPostgreSql</c>, <c>AddNpgsqlDataSource</c>, EF Core's <c>UseNpgsql</c>),
/// where pooling is the point. Opening an unpooled session costs a full connection handshake every time.
/// </para>
/// <para>
/// <b>Clearing a pool</b> (for example before dropping a test database that still has idle pooled connectors)
/// must happen on the data source the application actually uses — <c>dataSource.Clear()</c> — not through a
/// connection string: <c>ClearPool(new NpgsqlConnection(cs))</c> reaches only the registry's own source for
/// that string, never the application's, and leaks a new registry entry on top.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Create a per-test database through a short-lived admin session, then drop it when the test is done.
/// await using (var admin = await UnpooledNpgsqlSession.OpenAsync(adminConnectionString, ct)) {
///     await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template_app", admin.Connection);
///     await create.ExecuteNonQueryAsync(ct);
/// }
/// </code>
/// </example>
public sealed class UnpooledNpgsqlSession : IAsyncDisposable {
    private readonly NpgsqlDataSource _dataSource;

    private UnpooledNpgsqlSession(NpgsqlDataSource dataSource, NpgsqlConnection connection) {
        _dataSource = dataSource;
        Connection = connection;
    }

    /// <summary>
    /// The open connection. Owned by this session: dispose the session, not the connection, so the private data
    /// source goes with it.
    /// </summary>
    public NpgsqlConnection Connection { get; }

    /// <summary>
    /// Opens a dedicated, unpooled connection to the database <paramref name="connectionString"/> names.
    /// </summary>
    /// <param name="connectionString">
    /// The connection string. Any <c>Pooling</c> setting in it is overridden to <see langword="false"/>; every
    /// other setting (search path, timeouts, SSL, application name) applies as given.
    /// </param>
    /// <param name="cancellationToken">Cancels opening the connection.</param>
    /// <returns>The open session; dispose it to close the server session.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null, empty, or whitespace.</exception>
    public static async Task<UnpooledNpgsqlSession> OpenAsync(string connectionString,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        // The full builder, not the slim one: the session offers the same type mappings a
        // `new NpgsqlConnection(connectionString)` would, so it is a drop-in replacement for one.
        var dataSource = NpgsqlDataSource.Create(builder);
        try {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            return new UnpooledNpgsqlSession(dataSource, connection);
        }
        catch {
            await dataSource.DisposeAsync();
            throw;
        }
    }

    /// <summary>Closes the connection, which ends the server session, and disposes the private data source.</summary>
    public async ValueTask DisposeAsync() {
        await Connection.DisposeAsync();
        await _dataSource.DisposeAsync();
    }
}
