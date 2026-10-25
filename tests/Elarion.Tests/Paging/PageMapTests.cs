using AwesomeAssertions;
using Elarion.Abstractions.Paging;
using Xunit;

namespace Elarion.Tests.Paging;

public sealed class PageMapTests {
    private static readonly Page<int> Keyset = new() {
        Items = [1, 2, 3],
        StartCursor = "start",
        EndCursor = "end",
        HasNext = true,
        HasPrevious = true
    };

    [Fact]
    public void Map_ProjectsEveryItemAndKeepsTheMetadata() {
        var offset = new Page<int> { Items = [4, 5], HasNext = true, Total = 12 };

        var mapped = offset.Map(i => $"#{i}");

        mapped.Items.Should().Equal("#4", "#5");
        mapped.HasNext.Should().BeTrue();
        mapped.HasPrevious.Should().BeFalse();
        mapped.Total.Should().Be(12);
        mapped.StartCursor.Should().BeNull();
    }

    [Fact]
    public void Map_KeepsKeysetCursors() {
        var mapped = Keyset.Map(i => i * 10);

        mapped.Should().BeEquivalentTo(new Page<int> {
            Items = [10, 20, 30], StartCursor = "start", EndCursor = "end", HasNext = true, HasPrevious = true
        });
    }

    [Fact]
    public async Task MapAsync_ProjectsTheWholeListInOneCall() {
        var calls = 0;

        var mapped = await Keyset.MapAsync<string>((items, _) => {
            calls++;
            return ValueTask.FromResult<IReadOnlyList<string>>(items.Select(i => $"row {i}").ToArray());
        }, TestContext.Current.CancellationToken);

        calls.Should().Be(1);
        mapped.Items.Should().Equal("row 1", "row 2", "row 3");
        mapped.EndCursor.Should().Be("end");
        mapped.HasPrevious.Should().BeTrue();
    }

    [Fact]
    public async Task MapAsync_SelectorDroppingItems_Throws() {
        var act = async () => await Keyset.MapAsync<int>(
            (items, _) => ValueTask.FromResult<IReadOnlyList<int>>(items.Skip(1).ToArray()),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 2 items for a page of 3*");
    }
}
