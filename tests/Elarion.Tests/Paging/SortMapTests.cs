using AwesomeAssertions;
using Elarion.EntityFrameworkCore.Paging;
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
}
