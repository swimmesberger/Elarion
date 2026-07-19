using System.Linq.Expressions;
using AwesomeAssertions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Paging;
using Elarion.Paging;
using Elarion.Tests.Authorization;
using Xunit;

namespace Elarion.Tests.Paging;

public sealed class QueryableAuthorizationExtensionsTests {
    private static readonly Guid OwnerA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OwnerB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly ICurrentUser UserA =
        new FakeCurrentUser { IsAuthenticated = true, UserId = OwnerA.ToString() };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void WhereAuthorized_NullFilter_LeavesSourceUnchanged() {
        var source = Docs();

        var result = source.WhereAuthorized(new FakeQueryAuthorizer<Doc>(null), UserA);

        result.Should().BeSameAs(source);
    }

    [Fact]
    public void WhereAuthorized_DenyAll_YieldsEmpty() {
        var result = Docs().WhereAuthorized(new FakeQueryAuthorizer<Doc>(_ => false), UserA);

        result.ToList().Should().BeEmpty();
    }

    [Fact]
    public void WhereAuthorized_OwnerPredicate_FiltersToOwnedRows() {
        var result = Docs().WhereAuthorized(new FakeQueryAuthorizer<Doc>(d => d.OwnerId == OwnerA), UserA);

        result.ToList().Should().HaveCount(2).And.OnlyContain(d => d.OwnerId == OwnerA);
    }

    [Fact]
    public void WhereAuthorized_DefaultsToReadOperation() {
        var authorizer = new FakeQueryAuthorizer<Doc>(null);

        Docs().WhereAuthorized(authorizer, UserA);

        authorizer.LastOperation.Should().Be(ResourceOperation.Read);
    }

    [Fact]
    public void WhereAuthorized_PassesThroughExplicitOperation() {
        var authorizer = new FakeQueryAuthorizer<Doc>(null);

        Docs().WhereAuthorized(authorizer, UserA, ResourceOperation.Update);

        authorizer.LastOperation.Should().Be(ResourceOperation.Update);
    }

    [Fact]
    public async Task WhereAuthorized_ComposesBeforeOffsetPaging_CountsAndPagesOnlyAuthorizedRows() {
        var page = await Docs()
            .WhereAuthorized(new FakeQueryAuthorizer<Doc>(d => d.OwnerId == OwnerA), UserA)
            .ToOffsetPageAsync(
                new OffsetPageRequest { Page = 1, Size = 10 },
                d => d,
                SortMap<Doc>.CreateBuilder("id", d => d.Id).Build(),
                cancellationToken: Token);

        // The predicate is part of the query, so Total reflects only authorized rows — not the full table.
        page.Total.Should().Be(2);
        page.Items.Should().OnlyContain(d => d.OwnerId == OwnerA);
    }

    [Fact]
    public void Matches_NullFilter_ReturnsTrue() {
        new FakeQueryAuthorizer<Doc>(null)
            .Matches(new Doc(Guid.NewGuid(), OwnerB), UserA)
            .Should().BeTrue();
    }

    [Fact]
    public void Matches_OwnerPredicate_TrueForOwnerFalseOtherwise() {
        var authorizer = new FakeQueryAuthorizer<Doc>(d => d.OwnerId == OwnerA);

        authorizer.Matches(new Doc(Guid.NewGuid(), OwnerA), UserA).Should().BeTrue();
        authorizer.Matches(new Doc(Guid.NewGuid(), OwnerB), UserA).Should().BeFalse();
    }

    private static IQueryable<Doc> Docs() {
        return new[] {
            new Doc(Guid.NewGuid(), OwnerA),
            new Doc(Guid.NewGuid(), OwnerB),
            new Doc(Guid.NewGuid(), OwnerA)
        }.AsAsyncQueryable();
    }

    private sealed record Doc(Guid Id, Guid OwnerId);

    private sealed class FakeQueryAuthorizer<TEntity>(Expression<Func<TEntity, bool>>? filter)
        : IQueryAuthorizer<TEntity>
        where TEntity : class {
        public ResourceOperation? LastOperation { get; private set; }

        public Expression<Func<TEntity, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation) {
            LastOperation = operation;
            return filter;
        }
    }
}
