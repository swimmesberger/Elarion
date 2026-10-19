using AwesomeAssertions;
using Elarion.Sql.PostgreSql;
using Elarion.Tests.Migrations;
using Npgsql;
using Xunit;

namespace Elarion.Tests.SqlMapping;

/// <summary>
/// <see cref="UnpooledNpgsqlSession"/> against real PostgreSQL: it never registers anything in Npgsql's static
/// pool registry (where <c>new NpgsqlConnection(connectionString)</c> leaks one data source per distinct string),
/// and disposing it really ends the server session, so session-scoped state such as an advisory lock is gone.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnpooledNpgsqlSessionIntegrationTests(PostgreSqlMigrationsFixture fixture)
    : IClassFixture<PostgreSqlMigrationsFixture> {
    private const long LockKey = 0x5E55_10E0;

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenAndDispose_DistinctConnectionStrings_LeaveThePoolRegistryUntouched() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var markers = Enumerable.Range(0, 3).Select(_ => $"unpooled-{Guid.CreateVersion7():N}").ToArray();

        foreach (var marker in markers) {
            await using var session = await UnpooledNpgsqlSession.OpenAsync(
                NpgsqlPoolRegistry.Mark(connectionString, marker), TestToken);
            await using var command = new NpgsqlCommand("SELECT current_setting('application_name')", session.Connection);
            (await command.ExecuteScalarAsync(TestToken)).Should().Be(marker);
        }

        markers.Should().AllSatisfy(marker => NpgsqlPoolRegistry.Contains(marker).Should().BeFalse());
    }

    [Fact]
    public async Task ConnectionStringConstructor_RegistersAPool_WhichIsWhatTheSessionAvoids() {
        // Guards the probe: if Npgsql stopped registering here, the test above would pass vacuously.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var marker = $"pooled-{Guid.CreateVersion7():N}";

        await using (var connection = new NpgsqlConnection(
                         new NpgsqlConnectionStringBuilder(NpgsqlPoolRegistry.Mark(connectionString, marker)) {
                             Pooling = false
                         }.ConnectionString)) {
            await connection.OpenAsync(TestToken);
        }

        NpgsqlPoolRegistry.Contains(marker).Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_EndsTheServerSession_ReleasingItsAdvisoryLock() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);

        int pid;
        var holder = await UnpooledNpgsqlSession.OpenAsync(connectionString, TestToken);
        await using (holder) {
            pid = (int)(await ScalarAsync(holder.Connection, "SELECT pg_backend_pid()"))!;
            (await ScalarAsync(holder.Connection, $"SELECT pg_try_advisory_lock({LockKey})")).Should().Be(true);
        }

        await using var observer = await UnpooledNpgsqlSession.OpenAsync(connectionString, TestToken);
        // The backend exits asynchronously after the client's Terminate message; wait for it, bounded.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((long)(await ScalarAsync(observer.Connection, $"SELECT count(*) FROM pg_stat_activity WHERE pid = {pid}"))! > 0) {
            DateTime.UtcNow.Should().BeBefore(deadline, "the disposed session's backend should exit");
            await Task.Delay(50, TestToken);
        }

        (await ScalarAsync(observer.Connection, $"SELECT pg_try_advisory_lock({LockKey})")).Should().Be(true);
    }

    [Fact]
    public async Task Dispose_ClosesTheConnection() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);

        var session = await UnpooledNpgsqlSession.OpenAsync(connectionString, TestToken);
        session.Connection.State.Should().Be(System.Data.ConnectionState.Open);
        await session.DisposeAsync();

        session.Connection.State.Should().Be(System.Data.ConnectionState.Closed);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql) {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestToken);
    }
}
