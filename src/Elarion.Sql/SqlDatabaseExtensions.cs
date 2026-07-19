namespace Elarion.Sql;

/// <summary>
/// Session access directly on the <see cref="ISqlDatabase"/> handle. An extension rather than an interface
/// member, so a custom database implementation (a tenant or replica router) only supplies
/// <see cref="ISqlDatabase.GetDataSource"/> and inherits the ergonomics.
/// </summary>
public static class SqlDatabaseExtensions {
    /// <summary>
    /// Opens an <b>owning</b> one-shot <see cref="ISqlSession"/> over a fresh pooled connection from the
    /// database — disposing the session returns the connection. Calls run autonomously (per-call auto-commit,
    /// no unit of work): the pattern for singleton-eligible handlers, which cannot inject the scoped
    /// <see cref="ISqlSession"/> — hold the singleton <see cref="ISqlDatabase"/> and open a session per
    /// operation. Inside a scoped handler, inject <see cref="ISqlSession"/> instead so writes join the framework
    /// unit of work.
    /// </summary>
    /// <example>
    /// <code>
    /// public sealed class WorldTick(ISqlDatabase db) {   // singleton-eligible: no scoped dependencies
    ///     public async ValueTask&lt;Result&gt; HandleAsync(Tick tick, CancellationToken ct) {
    ///         await using var session = await db.OpenSessionAsync(ct);
    ///         var rows = await session.QueryAsync&lt;Reading&gt;($"{Reading.Select} WHERE cell = {tick.Cell}", ct);
    ///         // …
    ///     }
    /// }
    /// </code>
    /// </example>
    public static async ValueTask<ISqlSession> OpenSessionAsync(
        this ISqlDatabase database, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(database);
        var connection = await database.GetDataSource().OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new ConnectionSqlSession(connection, transaction: null, ownsConnection: true);
    }
}
