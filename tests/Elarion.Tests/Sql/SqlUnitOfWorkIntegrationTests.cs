using AwesomeAssertions;
using Elarion.Abstractions.Pipeline;
using Elarion.Sql;
using Npgsql;
using Xunit;

namespace Elarion.Tests.SqlMapping;

/// <summary>
/// Integration tests for the EF-free SQL-tier unit of work (<see cref="ISqlSession"/> + <c>SqlUnitOfWork</c>)
/// against real PostgreSQL: a handler's raw-SQL writes commit or roll back atomically on the session's shared
/// connection, reads inside the scope observe its own uncommitted writes, a nested scope joins the ambient
/// transaction via a savepoint, and a requested lock timeout is applied and restored.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqlUnitOfWorkIntegrationTests(PostgreSqlSqlSessionFixture fixture)
    : IClassFixture<PostgreSqlSqlSessionFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A session over the fixture's data source through the default single-source provider — the seam the DI
    // helpers register for a single-database host.
    private SqlSession NewSession() {
        return new SqlSession(new SingletonSqlDataSourceProvider(fixture.DataSource));
    }

    private async Task<bool> ExistsAsync(Guid id) {
        // Verify through an independent connection so we observe only committed state.
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM sql_widgets WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync(Ct))! == 1;
    }

    [Fact]
    public async Task Commit_PersistsTheHandlersWrites() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
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
    public async Task Rollback_DiscardsTheHandlersWrites() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
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
    public async Task DisposeWithoutCommit_RollsBack() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = Guid.NewGuid();

        await using (var session = NewSession()) {
            var uow = new SqlUnitOfWork(session);
            // No explicit commit or rollback: leaving the scope must roll the transaction back.
            await using var scope = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);
            await session.InsertAsync(new SqlWidget { Id = id, Name = "abandoned" }, Ct);
        }

        (await ExistsAsync(id)).Should().BeFalse();
    }

    [Fact]
    public async Task ReadInsideScope_SeesItsOwnUncommittedWrite() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = Guid.NewGuid();

        await using var session = NewSession();
        var uow = new SqlUnitOfWork(session);
        await using var scope = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);
        await session.InsertAsync(new SqlWidget { Id = id, Name = "in-flight" }, Ct);

        // The read runs on the same shared connection inside the open transaction, so it must see the write the
        // scope has not committed yet — proof the session's connection is the one the transaction is open on.
        var found = await session.QueryFirstOrDefaultAsync<SqlWidget>(
            $"SELECT id, name FROM sql_widgets WHERE id = {id}", Ct);
        found.Should().NotBeNull();
        found!.Name.Should().Be("in-flight");

        // ...while an independent connection cannot see it until commit.
        (await ExistsAsync(id)).Should().BeFalse();
        await scope.RollbackAsync(Ct);
    }

    [Fact]
    public async Task NestedScope_JoinsAmbientTransaction_BothCommit() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var outerId = Guid.NewGuid();
        var innerId = Guid.NewGuid();

        await using (var session = NewSession()) {
            var uow = new SqlUnitOfWork(session);
            await using var outer = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);
            await session.InsertAsync(new SqlWidget { Id = outerId, Name = "outer" }, Ct);

            // A nested transactional handler on the same scope (as through IHandlerSender) must not throw
            // "transaction already in progress": it joins the ambient transaction with a savepoint.
            await using (var inner = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct)) {
                await session.InsertAsync(new SqlWidget { Id = innerId, Name = "inner" }, Ct);
                await inner.CommitAsync(Ct);
            }

            await outer.CommitAsync(Ct);
        }

        (await ExistsAsync(outerId)).Should().BeTrue();
        (await ExistsAsync(innerId)).Should().BeTrue();
    }

    [Fact]
    public async Task NestedScope_Rollback_DiscardsOnlyInnerWrites() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var outerId = Guid.NewGuid();
        var innerId = Guid.NewGuid();

        await using (var session = NewSession()) {
            var uow = new SqlUnitOfWork(session);
            await using var outer = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);
            await session.InsertAsync(new SqlWidget { Id = outerId, Name = "outer" }, Ct);

            await using (var inner = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct)) {
                await session.InsertAsync(new SqlWidget { Id = innerId, Name = "inner" }, Ct);
                // Inner fails: roll back to the savepoint, discarding only the inner write.
                await inner.RollbackAsync(Ct);
            }

            await outer.CommitAsync(Ct);
        }

        (await ExistsAsync(outerId)).Should().BeTrue();
        (await ExistsAsync(innerId)).Should().BeFalse();
    }

    [Fact]
    public async Task RootScope_AppliesLockTimeout() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        await using var session = NewSession();
        var uow = new SqlUnitOfWork(session);
        await using var scope = await uow.BeginAsync(
            new UnitOfWorkOptions { LockTimeout = TimeSpan.FromSeconds(2) }, Ct);

        (await CurrentLockTimeoutAsync(session)).Should().Be("2s");
        await scope.RollbackAsync(Ct);
    }

    [Fact]
    public async Task NestedScope_AppliesLockTimeout_AndCommitRestoresTheAmbientValue() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        await using var session = NewSession();
        var uow = new SqlUnitOfWork(session);
        await using var outer = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);
        var ambient = await CurrentLockTimeoutAsync(session);

        await using (var inner = await uow.BeginAsync(
                         new UnitOfWorkOptions { LockTimeout = TimeSpan.FromSeconds(1) }, Ct)) {
            (await CurrentLockTimeoutAsync(session)).Should().Be("1s");
            await inner.CommitAsync(Ct);
        }

        // SET LOCAL persists to the end of the physical transaction — the nested commit must restore the ambient
        // value so the outer scope's statements are unaffected.
        (await CurrentLockTimeoutAsync(session)).Should().Be(ambient);
        await outer.CommitAsync(Ct);
    }

    private static Task<string?> CurrentLockTimeoutAsync(ISqlSession session) {
        return session.ExecuteScalarAsync<string>($"SELECT current_setting('lock_timeout')", Ct);
    }
}
