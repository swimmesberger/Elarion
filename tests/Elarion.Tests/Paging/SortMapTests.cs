using AwesomeAssertions;
using Elarion.Paging;
using Xunit;

namespace Elarion.Tests.Paging;

public sealed class SortMapTests
{
    private sealed record Row(int Id, string Name);

    private static readonly Row[] Rows =
    [
        new(1, "charlie"),
        new(2, "alice"),
        new(3, "bob"),
    ];

    [Fact]
    public void Apply_NoSort_UsesDefaultKeyAscending()
    {
        var map = SortMap<Row>.CreateBuilder("id", r => r.Id).Add("name", r => r.Name).Build();

        var ids = map.Apply(Rows.AsQueryable(), null).Select(r => r.Id).ToArray();

        ids.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Apply_NamedKeyDescending_SortsByThatKey()
    {
        var map = SortMap<Row>.CreateBuilder("id", r => r.Id).Add("name", r => r.Name).Build();

        var names = map.Apply(Rows.AsQueryable(), "-name").Select(r => r.Name).ToArray();

        names.Should().Equal("charlie", "bob", "alice");
    }

    [Fact]
    public void Apply_KeyIsCaseInsensitive()
    {
        var map = SortMap<Row>.CreateBuilder("id", r => r.Id).Add("name", r => r.Name).Build();

        var names = map.Apply(Rows.AsQueryable(), "-NAME").Select(r => r.Name).ToArray();

        names.Should().Equal("charlie", "bob", "alice");
    }

    [Fact]
    public void Apply_UnknownKey_FallsBackToDefaultAscending()
    {
        var map = SortMap<Row>.CreateBuilder("id", r => r.Id).Add("name", r => r.Name).Build();

        var ids = map.Apply(Rows.AsQueryable(), "-unknown").Select(r => r.Id).ToArray();

        ids.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Build_SnapshotsBuilder_LaterMutationDoesNotLeak()
    {
        var builder = SortMap<Row>.CreateBuilder("id", r => r.Id);
        var map = builder.Build();
        builder.Add("name", r => r.Name);

        var ids = map.Apply(Rows.AsQueryable(), "name").Select(r => r.Id).ToArray();

        ids.Should().Equal(1, 2, 3);
    }

    private sealed record RankedRow(int Id, int Rank);

    private static readonly RankedRow[] RankedRows =
    [
        new(1, 5),
        new(2, 5),
        new(3, 1),
    ];

    [Fact]
    public void Apply_DeclaredDescendingDefault_OrdersDescending()
    {
        var map = SortMap<RankedRow>.CreateBuilder("id", r => r.Id, SortDirection.Descending).Build();

        var ids = map.Apply(RankedRows.AsQueryable(), null).Select(r => r.Id).ToArray();

        ids.Should().Equal(3, 2, 1);
    }

    [Fact]
    public void Apply_PlusPrefix_OverridesDeclaredDescendingToAscending()
    {
        var map = SortMap<RankedRow>.CreateBuilder("id", r => r.Id, SortDirection.Descending).Build();

        var ids = map.Apply(RankedRows.AsQueryable(), "+id").Select(r => r.Id).ToArray();

        ids.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Apply_CompositeTiebreaker_BreaksTiesByTrailingColumn()
    {
        var map = SortMap<RankedRow>.CreateBuilder("rank", r => r.Rank).ThenBy(r => r.Id).Build();

        var ids = map.Apply(RankedRows.AsQueryable(), "rank").Select(r => r.Id).ToArray();

        // Rank ascending puts row 3 (rank 1) first; the rank-5 tie is broken by Id ascending.
        ids.Should().Equal(3, 1, 2);
    }

    [Fact]
    public void Apply_MinusPrefix_FlipsPrimaryButLeavesTiebreakerFixed()
    {
        var map = SortMap<RankedRow>.CreateBuilder("rank", r => r.Rank).ThenBy(r => r.Id).Build();

        var ids = map.Apply(RankedRows.AsQueryable(), "-rank").Select(r => r.Id).ToArray();

        // Rank descending puts the rank-5 group first; its tiebreaker stays Id ascending (1, 2), then rank 1.
        ids.Should().Equal(1, 2, 3);
    }
}
