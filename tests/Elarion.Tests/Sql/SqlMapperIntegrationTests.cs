using AwesomeAssertions;
using Elarion.Abstractions.Serialization;
using Elarion.Sql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.SqlMapping;

/// <summary>
/// End-to-end tests for the generated <c>[SqlRecord]</c> mappers against real PostgreSQL: every
/// supported value shape round-trips through <c>BindParameters</c> + <c>QueryAsync</c>, the generated
/// constants compose hand-written SQL, interpolation binds parameters (including IN expansion), and
/// the generated DI registration resolves.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqlMapperIntegrationTests(PostgreSqlSqlMapperFixture fixture)
    : IClassFixture<PostgreSqlSqlMapperFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset FixedInstant = new(2026, 7, 13, 8, 15, 30, TimeSpan.Zero);

    private static readonly SqlItemSqlMapper Mapper = new(CreateJsonSerialization());

    private static IElarionJsonSerialization CreateJsonSerialization() {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(SqlTestJsonContext.Default));
        return services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>();
    }

    private static SqlItem NewItem(int index) => new() {
        Id = Guid.CreateVersion7(),
        Name = $"item-{index}",
        Note = index % 3 == 0 ? null : $"note {index}",
        Quantity = index,
        Sequence = index * 37L,
        Price = 10.25m + index,
        Active = index % 2 == 0,
        CreatedAt = FixedInstant.AddSeconds(index),
        Status = (SqlItemStatus)(index % 3),
        PreviousStatus = index % 4 == 0 ? null : (SqlItemStatus)((index + 1) % 3),
        Payload = index % 5 == 0 ? null : [1, 2, (byte)index],
        DueOn = index % 6 == 0 ? null : new DateOnly(2026, 7, 1).AddDays(index),
        Profile = index % 3 == 0 ? null : new SqlItemProfile { Color = $"color-{index}", Weight = index },
        Transient = "never persisted",
    };

    private async Task<List<SqlItem>> InsertItemsAsync(int count) {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(Ct);
        await connection.ExecuteAsync($"TRUNCATE TABLE sql_items", Ct);

        var items = Enumerable.Range(0, count).Select(NewItem).OrderBy(i => i.Id).ToList();
        foreach (var item in items) {
            await using var command = connection.CreateCommand();
            command.CommandText = SqlItemSqlMapper.Insert;
            Mapper.BindParameters(command, item);
            await command.ExecuteNonQueryAsync(Ct);
        }

        return items;
    }

    [Fact]
    public async Task RoundTrip_AllValueShapes() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var items = await InsertItemsAsync(25);

        await using var connection = fixture.CreateConnection();
        var read = await connection.QueryAsync(
            Mapper,
            $"SELECT {SqlItemSqlMapper.Columns.All:raw} FROM {SqlItemSqlMapper.TableName:raw}",
            Ct);

        read.Should().BeEquivalentTo(items.Select(i => i with { Transient = null }));
    }

    [Fact]
    public async Task Interpolation_BindsScalarsAndExpandsCollections() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var items = await InsertItemsAsync(10);
        var wanted = items.Take(3).Select(i => i.Id).ToArray();
        var minQuantity = -1;

        await using var connection = fixture.CreateConnection();
        var read = await connection.QueryAsync(
            Mapper,
            $"""
             SELECT {SqlItemSqlMapper.Columns.All:raw} FROM {SqlItemSqlMapper.TableName:raw}
             WHERE id IN {wanted} AND quantity > {minQuantity}
             """,
            Ct);

        read.Select(i => i.Id).Should().BeEquivalentTo(wanted);
    }

    [Fact]
    public async Task Interpolation_EmptyInList_FailsBeforeTouchingTheDatabase() {
        await using var connection = fixture.CreateConnection();

        var act = () => connection.QueryAsync(
            Mapper,
            $"SELECT {SqlItemSqlMapper.Columns.All:raw} FROM sql_items WHERE id IN {Array.Empty<Guid>()}",
            Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*empty collection*");
    }

    [Fact]
    public async Task FragmentComposition_ReusesWherePiece() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var items = await InsertItemsAsync(10);
        var active = new SqlStatement($"WHERE active = {true}");

        await using var connection = fixture.CreateConnection();
        var count = await connection.ExecuteScalarAsync<long>(
            new SqlStatement($"SELECT count(*) FROM sql_items {active}"), Ct);
        var read = await connection.QueryAsync(
            Mapper, new SqlStatement($"SELECT {SqlItemSqlMapper.Columns.All:raw} FROM sql_items {active} ORDER BY id"), Ct);

        count.Should().Be(items.Count(i => i.Active));
        read.Should().HaveCount((int)count);
        read.Should().OnlyContain(i => i.Active);
    }

    [Fact]
    public async Task QueryFirstOrDefault_MissReturnsNull() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        await InsertItemsAsync(2);

        await using var connection = fixture.CreateConnection();
        var missing = await connection.QueryFirstOrDefaultAsync(
            Mapper, $"SELECT {SqlItemSqlMapper.Columns.All:raw} FROM sql_items WHERE id = {Guid.NewGuid()}", Ct);

        missing.Should().BeNull();
    }

    [Fact]
    public async Task Execute_UpdatesRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var items = await InsertItemsAsync(5);
        var target = items[2];

        await using var connection = fixture.CreateConnection();
        var affected = await connection.ExecuteAsync(
            $"UPDATE sql_items SET name = {"renamed"} WHERE id = {target.Id}", Ct);
        var renamed = await connection.QueryFirstOrDefaultAsync(
            Mapper, $"SELECT {SqlItemSqlMapper.Columns.All:raw} FROM sql_items WHERE id = {target.Id}", Ct);

        affected.Should().Be(1);
        renamed!.Name.Should().Be("renamed");
    }

    [Fact]
    public async Task PositionalRecord_RoundTrips() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var row = new SqlPositionalRow(Guid.CreateVersion7(), "row-1", 7);

        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(Ct);
        await connection.ExecuteAsync($"TRUNCATE TABLE sql_positional_row", Ct);
        await using (var command = connection.CreateCommand()) {
            command.CommandText =
                $"INSERT INTO {SqlPositionalRowSqlMapper.TableName} ({SqlPositionalRowSqlMapper.Columns.All}) "
                + $"VALUES ({SqlPositionalRowSqlMapper.Columns.AllParameters})";
            SqlPositionalRowSqlMapper.Instance.BindParameters(command, row);
            await command.ExecuteNonQueryAsync(Ct);
        }

        var read = await connection.QueryAsync(
            SqlPositionalRowSqlMapper.Instance,
            $"SELECT {SqlPositionalRowSqlMapper.Columns.All:raw} FROM {SqlPositionalRowSqlMapper.TableName:raw}",
            Ct);

        read.Should().Equal(row);
    }

    [Fact]
    public void GeneratedRegistration_ResolvesMappers() {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(SqlTestJsonContext.Default));
        services.AddElarionSqlMappers();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISqlRowMapper<SqlItem>>().Should().BeOfType<SqlItemSqlMapper>();
        provider.GetRequiredService<ISqlRowMapper<SqlPositionalRow>>()
            .Should().BeSameAs(SqlPositionalRowSqlMapper.Instance);
    }

    [Fact]
    public void GeneratedConstants_MatchConventions() {
        SqlItemSqlMapper.TableName.Should().Be("sql_items");
        SqlItemSqlMapper.Insert.Should().StartWith("INSERT INTO sql_items (id, name, note_text,");
        SqlItemSqlMapper.Select.Should().StartWith("SELECT id, name, note_text,").And.EndWith("FROM sql_items");
        SqlItemSqlMapper.Columns.AllAssignments.Should().Contain("note_text = @note_text");
        SqlItemSqlMapper.Columns.Note.Should().Be("note_text", "[SqlColumn] overrides the snake_case default");
        SqlItemSqlMapper.Columns.CreatedAt.Should().Be("created_at");
        SqlItemSqlMapper.Columns.All.Should().NotContain("transient", "[SqlIgnore] excludes the property");
        SqlItemSqlMapper.Columns.All.Should().NotContain("is_named", "derived members are skipped");
        SqlPositionalRowSqlMapper.TableName.Should().Be("sql_positional_row");
    }
}
