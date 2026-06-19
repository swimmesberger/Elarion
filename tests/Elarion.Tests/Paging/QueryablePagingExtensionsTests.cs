using System.Linq.Expressions;
using AwesomeAssertions;
using Elarion.Abstractions.Paging;
using Elarion.EntityFrameworkCore.Paging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.Paging;

public sealed class QueryablePagingExtensionsTests
{
    private static readonly DateTime BaseTime = new(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ToKeysetPageAsync_FirstPage_ReturnsOrderedHeadWithNextCursor()
    {
        var data = Items(10).AsAsyncQueryable();

        var page = await data.ToKeysetPageAsync(
            new KeysetPageRequest { Size = 3 }, TestEntityKeyset.Definition, Project, cancellationToken: Token);

        page.Items.Select(i => i.Name).Should().Equal("item0", "item1", "item2");
        page.HasPrevious.Should().BeFalse();
        page.HasNext.Should().BeTrue();
        page.EndCursor.Should().NotBeNull();
        page.Total.Should().BeNull();
    }

    [Fact]
    public async Task ToKeysetPageAsync_AfterCursor_ReturnsNextPage()
    {
        var data = Items(10).AsAsyncQueryable();

        var first = await data.ToKeysetPageAsync(
            new KeysetPageRequest { Size = 3 }, TestEntityKeyset.Definition, Project, cancellationToken: Token);
        var second = await data.ToKeysetPageAsync(
            new KeysetPageRequest { After = first.EndCursor, Size = 3 }, TestEntityKeyset.Definition, Project, cancellationToken: Token);

        second.Items.Select(i => i.Name).Should().Equal("item3", "item4", "item5");
        second.HasPrevious.Should().BeTrue();
        second.HasNext.Should().BeTrue();
    }

    [Fact]
    public async Task ToKeysetPageAsync_BeforeCursor_ReturnsPriorPageInNaturalOrder()
    {
        var data = Items(10).AsAsyncQueryable();

        var first = await data.ToKeysetPageAsync(
            new KeysetPageRequest { Size = 3 }, TestEntityKeyset.Definition, Project, cancellationToken: Token);
        var second = await data.ToKeysetPageAsync(
            new KeysetPageRequest { After = first.EndCursor, Size = 3 }, TestEntityKeyset.Definition, Project, cancellationToken: Token);
        var back = await data.ToKeysetPageAsync(
            new KeysetPageRequest { Before = second.StartCursor, Size = 3 }, TestEntityKeyset.Definition, Project, cancellationToken: Token);

        back.Items.Select(i => i.Name).Should().Equal("item0", "item1", "item2");
        back.HasNext.Should().BeTrue();
        back.HasPrevious.Should().BeFalse();
    }

    [Fact]
    public async Task ToKeysetPageAsync_PageThroughAll_YieldsEveryItemOnceInOrder()
    {
        var data = Items(10).AsAsyncQueryable();
        var collected = new List<string>();
        string? after = null;

        while (true)
        {
            var page = await data.ToKeysetPageAsync(
                new KeysetPageRequest { After = after, Size = 4 }, TestEntityKeyset.Definition, Project, cancellationToken: Token);
            collected.AddRange(page.Items.Select(i => i.Name));
            if (!page.HasNext)
            {
                break;
            }

            after = page.EndCursor;
        }

        collected.Should().Equal(Enumerable.Range(0, 10).Select(i => $"item{i}"));
    }

    [Fact]
    public async Task ToKeysetPageAsync_EmptySource_ReturnsEmptyPage()
    {
        var data = Items(0).AsAsyncQueryable();

        var page = await data.ToKeysetPageAsync(
            new KeysetPageRequest { Size = 3 }, TestEntityKeyset.Definition, Project, cancellationToken: Token);

        page.Items.Should().BeEmpty();
        page.StartCursor.Should().BeNull();
        page.EndCursor.Should().BeNull();
        page.HasNext.Should().BeFalse();
        page.HasPrevious.Should().BeFalse();
    }

    [Fact]
    public async Task ToKeysetPageAsync_SizeExceedsMax_ClampsToMax()
    {
        var data = Items(10).AsAsyncQueryable();

        var page = await data.ToKeysetPageAsync(
            new KeysetPageRequest { Size = 1000 }, TestEntityKeyset.Definition, Project, maxSize: 5, cancellationToken: Token);

        page.Items.Should().HaveCount(5);
        page.HasNext.Should().BeTrue();
    }

    [Fact]
    public async Task ToOffsetPageAsync_FirstPage_IncludesTotalAndPaging()
    {
        var data = Items(10).AsAsyncQueryable();

        var page = await data.ToOffsetPageAsync(
            new OffsetPageRequest { Page = 1, Size = 3 }, Project, DefaultSort, cancellationToken: Token);

        page.Items.Select(i => i.Name).Should().Equal("item0", "item1", "item2");
        page.Total.Should().Be(10);
        page.HasPrevious.Should().BeFalse();
        page.HasNext.Should().BeTrue();
    }

    [Fact]
    public async Task ToOffsetPageAsync_LastPage_HasNoNext()
    {
        var data = Items(10).AsAsyncQueryable();

        var page = await data.ToOffsetPageAsync(
            new OffsetPageRequest { Page = 4, Size = 3 }, Project, DefaultSort, cancellationToken: Token);

        page.Items.Select(i => i.Name).Should().Equal("item9");
        page.Total.Should().Be(10);
        page.HasPrevious.Should().BeTrue();
        page.HasNext.Should().BeFalse();
    }

    [Fact]
    public async Task ToOffsetPageAsync_DescendingSort_OrdersByKeyDescending()
    {
        var data = Items(10).AsAsyncQueryable();

        var page = await data.ToOffsetPageAsync(
            new OffsetPageRequest { Page = 1, Size = 2, Sort = "-createdAt" }, Project, DefaultSort, cancellationToken: Token);

        page.Items.Select(i => i.Name).Should().Equal("item9", "item8");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SortMap<TestEntity> DefaultSort =>
        SortMap<TestEntity>.Create("createdAt", e => e.CreatedAt).Add("name", e => e.Name);

    private static readonly Expression<Func<TestEntity, TestDto>> Project = e => new TestDto(e.Id, e.Name);

    private static IEnumerable<TestEntity> Items(int count) =>
        Enumerable.Range(0, count).Select(i => new TestEntity
        {
            Id = new Guid(i, 0, 0, new byte[8]),
            CreatedAt = BaseTime.AddMinutes(i),
            Name = $"item{i}",
        });

    private sealed class TestEntity
    {
        public Guid Id { get; init; }
        public DateTime CreatedAt { get; init; }
        public string Name { get; init; } = "";
    }

    private sealed record TestDto(Guid Id, string Name);

    /// <summary>Hand-written mirror of the generator's emission for <c>[Keyset("CreatedAt", "Id")]</c>.</summary>
    private sealed class TestEntityKeyset : IKeysetDefinition<TestEntity>
    {
        public static TestEntityKeyset Definition { get; } = new();

        public IOrderedQueryable<TestEntity> ApplyOrder(IQueryable<TestEntity> source, bool forward)
            => forward
                ? source.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
                : source.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id);

        public Expression<Func<TestEntity, bool>>? BuildSeek(string cursor, bool forward)
        {
            if (!TryDecode(cursor, out var createdAt, out var id))
            {
                return null;
            }

            return forward
                ? e => e.CreatedAt > createdAt || (e.CreatedAt == createdAt && e.Id.CompareTo(id) > 0)
                : e => e.CreatedAt < createdAt || (e.CreatedAt == createdAt && e.Id.CompareTo(id) < 0);
        }

        public async Task<IReadOnlyList<KeysetEntry<TDto>>> ToEntriesAsync<TDto>(
            IQueryable<TestEntity> query, Expression<Func<TestEntity, TDto>> selector, CancellationToken cancellationToken)
        {
            var parameter = selector.Parameters[0];
            var projection = Expression.Lambda<Func<TestEntity, Projection<TDto>>>(
                Expression.New(
                    typeof(Projection<TDto>).GetConstructors()[0],
                    new Expression[]
                    {
                        selector.Body,
                        Expression.Property(parameter, nameof(TestEntity.CreatedAt)),
                        Expression.Property(parameter, nameof(TestEntity.Id)),
                    }),
                parameter);

            var rows = await query.Select(projection).ToListAsync(cancellationToken);
            var entries = new KeysetEntry<TDto>[rows.Count];
            for (var i = 0; i < rows.Count; i++)
            {
                var writer = new CursorWriter();
                writer.WriteDateTime(rows[i].Key0);
                writer.WriteGuid(rows[i].Key1);
                entries[i] = new KeysetEntry<TDto>(rows[i].Item, writer.ToCursor());
            }

            return entries;
        }

        private static bool TryDecode(string cursor, out DateTime createdAt, out Guid id)
        {
            createdAt = default;
            id = default;
            if (!CursorReader.TryCreate(cursor, out var reader))
            {
                return false;
            }

            return reader.TryReadDateTime(out createdAt) && reader.TryReadGuid(out id) && reader.AtEnd;
        }

        private sealed class Projection<TDto>
        {
            public Projection(TDto item, DateTime key0, Guid key1)
            {
                Item = item;
                Key0 = key0;
                Key1 = key1;
            }

            public TDto Item { get; }
            public DateTime Key0 { get; }
            public Guid Key1 { get; }
        }
    }
}
