using AwesomeAssertions;
using Elarion.Sql;
using Npgsql;
using Xunit;

namespace Elarion.Tests.SqlMapping;

/// <summary>
/// The <see cref="SqlConnectionSessionExtensions.AsSqlSession"/> bridge against real PostgreSQL: the one public
/// way onto the session surface for code that owns its connection. The transaction decision is made once at wrap
/// time — a wrap over the caller's transaction enlists every write, a bare wrap runs autonomously — and the view
/// is non-owning, so disposing it leaves the caller's connection untouched.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AsSqlSessionIntegrationTests(PostgreSqlSqlSessionFixture fixture)
    : IClassFixture<PostgreSqlSqlSessionFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<bool> ExistsAsync(Guid id) {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM sql_widgets WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync(Ct))! == 1;
    }

    [Fact]
    public async Task WrapWithTransaction_WritesEnlist_RollbackDiscardsThem() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = Guid.NewGuid();

        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(Ct);
        await using (var transaction = await connection.BeginTransactionAsync(Ct)) {
            var db = connection.AsSqlSession(transaction);
            await db.InsertAsync(new SqlWidget { Id = id, Name = "enlisted" }, Ct);
            // The write joined the caller's transaction, so rolling that back discards it — the semantics a
            // per-call transaction parameter used to make forgettable.
            await transaction.RollbackAsync(Ct);
        }

        (await ExistsAsync(id)).Should().BeFalse();
    }

    [Fact]
    public async Task WrapWithTransaction_CommitPersists() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = Guid.NewGuid();

        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(Ct);
        await using (var transaction = await connection.BeginTransactionAsync(Ct)) {
            var db = connection.AsSqlSession(transaction);
            await db.InsertAsync(new SqlWidget { Id = id, Name = "committed" }, Ct);
            await transaction.CommitAsync(Ct);
        }

        (await ExistsAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task BareWrap_RunsAutonomously() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = Guid.NewGuid();

        await using var connection = fixture.CreateConnection();
        var db = connection.AsSqlSession();
        db.CurrentTransaction.Should().BeNull();
        await db.InsertAsync(new SqlWidget { Id = id, Name = "autonomous" }, Ct);

        // Per-call auto-commit: the write is durable without any transaction ceremony.
        (await ExistsAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_IsNonOwning_ConnectionStaysUsable() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(Ct);
        await using (var db = (IAsyncDisposable)connection.AsSqlSession()) {
            _ = db; // dispose the view immediately
        }

        // The caller still owns a live connection.
        var count = await connection.AsSqlSession().ExecuteScalarAsync<long>(
            $"SELECT count(*) FROM sql_widgets", Ct);
        count.Should().BeGreaterThanOrEqualTo(0);
    }
}
