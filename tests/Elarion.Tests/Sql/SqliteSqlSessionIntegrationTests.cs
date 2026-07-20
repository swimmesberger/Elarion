using AwesomeAssertions;
using Elarion.Abstractions.Pipeline;
using Elarion.Migrations;
using Elarion.Sql;
using Elarion.Sql.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.SqlMapping;

/// <summary>
/// The EF-free SQL access tier over real SQLite (in-process, no Docker): the same <see cref="ISqlSession"/> +
/// <c>SqlUnitOfWork</c> a PostgreSQL host uses, proving the access tier is genuinely provider-portable — a
/// <c>[SqlRecord]</c> round-trips through the generated mapper on <c>Microsoft.Data.Sqlite</c>, and the unit of
/// work commits/rolls back on the session's connection. This is what makes <c>Elarion.Sql.Sqlite</c> a full
/// provider, not just a migration package.
/// </summary>
public sealed class SqliteSqlSessionIntegrationTests : IDisposable {
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "elarion_sqlite_sess_" + Guid.CreateVersion7().ToString("N") + ".db");

    private string ConnectionString =>
        new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ConnectionString;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SqliteSqlSessionIntegrationTests() {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE sql_widgets (id TEXT PRIMARY KEY, name TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    private SqlSession NewSession() {
        return new SqlSession(new DataSourceSqlDatabase(new SqliteDataSource(ConnectionString)));
    }

    private async Task<bool> ExistsAsync(Guid id) {
        // Read ids back as Guid (format-agnostic — Microsoft.Data.Sqlite stores Guid in its own TEXT format,
        // which GetFieldValue<Guid> reads back the same way the generated mapper does).
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM sql_widgets";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            if (reader.GetFieldValue<Guid>(0) == id) return true;

        return false;
    }

    [Fact]
    public async Task Commit_PersistsThroughTheGeneratedMapper() {
        var id = Guid.NewGuid();

        await using (var session = NewSession()) {
            var uow = new SqlUnitOfWork(session);
            await using var scope = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);
            await session.InsertAsync(new SqlWidget { Id = id, Name = "committed" }, Ct);
            await scope.CommitAsync(Ct);
        }

        (await ExistsAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task Rollback_DiscardsTheWrite() {
        var id = Guid.NewGuid();

        await using (var session = NewSession()) {
            var uow = new SqlUnitOfWork(session);
            await using var scope = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);
            await session.InsertAsync(new SqlWidget { Id = id, Name = "rolled-back" }, Ct);
            await scope.RollbackAsync(Ct);
        }

        (await ExistsAsync(id)).Should().BeFalse();
    }

    [Fact]
    public async Task ReadInsideScope_SeesItsOwnUncommittedWrite_ThenRoundTrips() {
        var id = Guid.NewGuid();

        await using var session = NewSession();
        var uow = new SqlUnitOfWork(session);
        await using var scope = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);
        await session.InsertAsync(new SqlWidget { Id = id, Name = "in-flight" }, Ct);

        // The generated mapper reads the row back on the same connection inside the transaction — the Guid
        // and text survive the SQLite round trip through GetFieldValue<T>.
        var found = await session.QueryFirstOrDefaultAsync<SqlWidget>(
            $"SELECT id, name FROM sql_widgets WHERE id = {id}", Ct);
        found.Should().NotBeNull();
        found!.Id.Should().Be(id);
        found.Name.Should().Be("in-flight");

        // ...while an independent connection sees nothing until commit.
        (await ExistsAsync(id)).Should().BeFalse();
        await scope.RollbackAsync(Ct);
    }

    [Fact]
    public async Task AddElarionSqlite_RegistersASession_AndTheMigrationFactory() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionSqlite(ConnectionString);
        services.AddElarionSqlUnitOfWork();

        await using var provider = services.BuildServiceProvider();
        provider.GetService<IMigrationDatabaseFactory>().Should().NotBeNull();

        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISqlSession>().Should().NotBeNull();
    }

    [Fact]
    public async Task SingletonHandlerPath_OpenSessionAsync_OnTheDatabaseHandle() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionSqlite(ConnectionString);
        await using var provider = services.BuildServiceProvider();

        // The singleton-eligible pattern end-to-end: a singleton service injects ISqlDatabase (no scoped
        // dependencies) and opens an owning one-shot session per operation — autonomous per-call semantics.
        var db = provider.GetRequiredService<ISqlDatabase>();
        var id = Guid.NewGuid();

        await using (var session = await db.OpenSessionAsync(Ct)) {
            session.CurrentTransaction.Should().BeNull();
            await session.InsertAsync(new SqlWidget { Id = id, Name = "one-shot" }, Ct);
        }

        // The session owned its connection (disposed with it); the write is durable and a fresh session reads
        // it back through the generated mapper.
        await using var verify = await db.OpenSessionAsync(Ct);
        var found = await verify.QueryFirstOrDefaultAsync<SqlWidget>(
            $"SELECT id, name FROM sql_widgets WHERE id = {id}", Ct);
        found.Should().NotBeNull();
        found!.Name.Should().Be("one-shot");
    }

    [Fact]
    public async Task BeginTransactionAsync_CommitMakesBothWritesDurable() {
        var db = new DataSourceSqlDatabase(new SqliteDataSource(ConnectionString));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // The transactional one-shot: two writes that must commit together, outside any unit of work — one
        // object, commit on it, no hand-assembled connection/transaction/wrap.
        await using (var tx = await db.BeginTransactionAsync(Ct)) {
            tx.CurrentTransaction.Should().NotBeNull();
            await tx.InsertAsync(new SqlWidget { Id = first, Name = "atomic-1" }, Ct);
            await tx.ExecuteAsync(
                $"INSERT INTO sql_widgets (id, name) VALUES ({second}, {"atomic-2"})", Ct);
            await tx.CommitAsync(Ct);
        }

        (await ExistsAsync(first)).Should().BeTrue();
        (await ExistsAsync(second)).Should().BeTrue();
    }

    [Fact]
    public async Task BeginTransactionAsync_DisposeWithoutCommit_RollsBackBothWrites() {
        var db = new DataSourceSqlDatabase(new SqliteDataSource(ConnectionString));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // No commit: leaving the scope (the failure path) must discard both writes together.
        await using (var tx = await db.BeginTransactionAsync(Ct)) {
            await tx.InsertAsync(new SqlWidget { Id = first, Name = "doomed-1" }, Ct);
            await tx.InsertAsync(new SqlWidget { Id = second, Name = "doomed-2" }, Ct);
        }

        (await ExistsAsync(first)).Should().BeFalse();
        (await ExistsAsync(second)).Should().BeFalse();
    }

    public void Dispose() {
        SqliteConnection.ClearAllPools();
        try {
            File.Delete(_path);
        }
        catch (IOException) {
            // Best-effort temp cleanup; the OS reclaims it regardless.
        }
    }
}
