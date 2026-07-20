using AwesomeAssertions;
using Elarion.Abstractions.Serialization;
using Elarion.Sql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.SqlMapping;

/// <summary>
/// End-to-end tests for the AOT-tier binary COPY path (ADR-0068) against real PostgreSQL: the
/// generated <c>INpgsqlCopyRecord</c> members stream every supported value shape, the staged upsert
/// behaviors merge with <c>ON CONFLICT</c>, misconfiguration fails before touching the database, and
/// the staged path enlists in the session's ambient transaction.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlCopyIntegrationTests(PostgreSqlSqlMapperFixture fixture)
    : IClassFixture<PostgreSqlSqlMapperFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset FixedInstant = new(2026, 7, 20, 9, 30, 0, TimeSpan.Zero);

    static PostgreSqlCopyIntegrationTests() {
        // The generated static WriteRowAsync serializes [SqlJson] columns through the mapper's static
        // Instance, which reads the ambient canonical accessor. Installing is idempotent per instance.
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(SqlTestJsonContext.Default));
        ElarionSqlJson.Use(services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>());
    }

    private static SqlItem NewItem(int index) {
        return new SqlItem {
            Id = Guid.CreateVersion7(),
            Name = $"copy-{index}",
            Note = index % 3 == 0 ? null : $"note {index}",
            Quantity = index,
            Sequence = index * 41L,
            Price = 7.75m + index,
            Active = index % 2 == 0,
            CreatedAt = FixedInstant.AddSeconds(index),
            Status = (SqlItemStatus)(index % 3),
            PreviousStatus = index % 4 == 0 ? null : (SqlItemStatus)((index + 1) % 3),
            Payload = index % 5 == 0 ? null : [3, 4, (byte)index],
            DueOn = index % 6 == 0 ? null : new DateOnly(2026, 7, 2).AddDays(index),
            Profile = index % 3 == 0 ? null : new SqlItemProfile { Color = $"copy-color-{index}", Weight = index },
            Transient = "never persisted"
        };
    }

    private async Task TruncateAsync(string table) {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(Ct);
        var db = connection.AsSqlSession();
        await db.ExecuteAsync($"TRUNCATE TABLE {SqlStatement.Verbatim(table)}", Ct);
    }

    [Fact]
    public async Task ExecuteInsert_Direct_RoundTripsAllValueShapes() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await TruncateAsync("sql_items");

        var items = Enumerable.Range(0, 50).Select(NewItem).ToList();

        await using var connection = fixture.CreateConnection();
        var db = connection.AsSqlSession();
        var written = await db.ExecuteInsertAsync(items, cancellationToken: Ct);

        written.Should().Be(50);
        var read = await db.QueryAsync<SqlItem>($"{SqlItem.Select}", Ct);
        read.Should().BeEquivalentTo(items.Select(i => i with { Transient = null }));
    }

    [Fact]
    public async Task ExecuteInsert_EmptyInput_TouchesNothing() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        await using var connection = fixture.CreateConnection();
        var db = connection.AsSqlSession();
        var written = await db.ExecuteInsertAsync(new List<SqlItem>(), cancellationToken: Ct);

        written.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteInsert_AsyncEnumerable_StreamsRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await TruncateAsync("sql_struct_points");

        await using var connection = fixture.CreateConnection();
        var db = connection.AsSqlSession();
        var written = await db.ExecuteInsertAsync(ProduceAsync(20), cancellationToken: Ct);

        written.Should().Be(20);
        var count = await db.ExecuteScalarAsync<long>($"SELECT count(*) FROM {SqlStructPoint.Table}", Ct);
        count.Should().Be(20);

        static async IAsyncEnumerable<SqlStructPoint> ProduceAsync(int count) {
            for (var i = 0; i < count; i++) {
                await Task.Yield();
                yield return new SqlStructPoint(Guid.CreateVersion7(), i * 1.5f, i * -0.5f);
            }
        }
    }

    [Fact]
    public async Task ExecuteInsert_StructRows_ReusedListBuffer_RoundTrips() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await TruncateAsync("sql_struct_points");

        var batch = new List<SqlStructPoint>();
        for (var i = 0; i < 100; i++) batch.Add(new SqlStructPoint(Guid.CreateVersion7(), i, 100 - i));

        await using var connection = fixture.CreateConnection();
        var db = connection.AsSqlSession();
        var written = await db.ExecuteInsertAsync(batch, cancellationToken: Ct);

        written.Should().Be(100);
        var read = await db.QueryAsync<SqlStructPoint>($"{SqlStructPoint.Select}", Ct);
        read.Should().BeEquivalentTo(batch);
    }

    [Fact]
    public async Task ExecuteInsert_DoNothing_SkipsExistingRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await TruncateAsync("sql_items");

        var existing = Enumerable.Range(0, 10).Select(NewItem).ToList();
        await using var connection = fixture.CreateConnection();
        var db = connection.AsSqlSession();
        await db.ExecuteInsertAsync(existing, cancellationToken: Ct);

        var fresh = Enumerable.Range(10, 5).Select(NewItem).ToList();
        var written = await db.ExecuteInsertAsync([.. existing, .. fresh],
            new SqlBulkInsertOptions { OnConflict = SqlBulkInsertConflictBehavior.DoNothing }, Ct);

        written.Should().Be(5);
        var count = await db.ExecuteScalarAsync<long>($"SELECT count(*) FROM {SqlItem.Table}", Ct);
        count.Should().Be(15);
    }

    [Fact]
    public async Task ExecuteInsert_Update_OverwritesOnConflictColumns() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await TruncateAsync("sql_items");

        var items = Enumerable.Range(0, 10).Select(NewItem).ToList();
        await using var connection = fixture.CreateConnection();
        var db = connection.AsSqlSession();
        await db.ExecuteInsertAsync(items, cancellationToken: Ct);

        var updated = items.Select(i => i with { Name = i.Name + "-v2", Quantity = i.Quantity + 100 }).ToList();
        var written = await db.ExecuteInsertAsync(updated, new SqlBulkInsertOptions {
            OnConflict = SqlBulkInsertConflictBehavior.Update,
            ConflictColumns = ["id"],
        }, Ct);

        written.Should().Be(10);
        var read = await db.QueryAsync<SqlItem>($"{SqlItem.Select}", Ct);
        read.Should().BeEquivalentTo(updated.Select(i => i with { Transient = null }));
    }

    [Fact]
    public async Task ExecuteInsert_Update_WithoutConflictColumns_FailsBeforeTheDatabase() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        await using var connection = fixture.CreateConnection();
        var db = connection.AsSqlSession();
        var act = () => db.ExecuteInsertAsync([NewItem(0)],
            new SqlBulkInsertOptions { OnConflict = SqlBulkInsertConflictBehavior.Update }, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ConflictColumns*");
    }

    [Fact]
    public async Task ExecuteInsert_UnknownConflictColumn_FailsBeforeTheDatabase() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        await using var connection = fixture.CreateConnection();
        var db = connection.AsSqlSession();
        var act = () => db.ExecuteInsertAsync([NewItem(0)], new SqlBulkInsertOptions {
            OnConflict = SqlBulkInsertConflictBehavior.Update,
            ConflictColumns = ["nope"],
        }, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a mapped column*");
    }

    [Fact]
    public async Task ExecuteInsert_StagedUpsert_RollsBackWithTheAmbientTransaction() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await TruncateAsync("sql_items");

        var items = Enumerable.Range(0, 5).Select(NewItem).ToList();

        await using (var connection = fixture.CreateConnection()) {
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            var db = connection.AsSqlSession(transaction);
            var written = await db.ExecuteInsertAsync(items, new SqlBulkInsertOptions {
                OnConflict = SqlBulkInsertConflictBehavior.Update,
                ConflictColumns = ["id"],
            }, Ct);
            written.Should().Be(5);

            await transaction.RollbackAsync(Ct);
        }

        await using var verifyConnection = fixture.CreateConnection();
        var verify = verifyConnection.AsSqlSession();
        var count = await verify.ExecuteScalarAsync<long>($"SELECT count(*) FROM {SqlItem.Table}", Ct);
        count.Should().Be(0);
    }
}
