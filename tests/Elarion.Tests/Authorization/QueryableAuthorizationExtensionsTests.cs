using System.Linq.Expressions;
using AwesomeAssertions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Paging;
using Elarion.Paging;
using Elarion.Tests.Paging;
using Xunit;

namespace Elarion.Tests.Authorization;

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

    [Fact]
    public void WhereAuthorizedVia_KeepsOnlyRowsWhoseOwnerIsVisible() {
        var docs = Docs().ToList();
        var notes = new[] {
            new Note(Guid.NewGuid(), docs[0].Id),
            new Note(Guid.NewGuid(), docs[1].Id),
            new Note(Guid.NewGuid(), docs[2].Id)
        }.AsQueryable();

        var visible = notes.WhereAuthorizedVia(
                n => n.DocId, docs.AsQueryable(), d => d.Id,
                new FakeQueryAuthorizer<Doc>(d => d.OwnerId == OwnerA), UserA)
            .ToList();

        visible.Select(n => n.DocId).Should().BeEquivalentTo([docs[0].Id, docs[2].Id]);
    }

    [Fact]
    public void WhereAuthorizedVia_UnrestrictedOwner_LeavesSourceUnchanged() {
        var notes = new[] { new Note(Guid.NewGuid(), Guid.NewGuid()) }.AsQueryable();

        notes.WhereAuthorizedVia(n => n.DocId, Docs(), d => d.Id, new FakeQueryAuthorizer<Doc>(null), UserA)
            .Should().BeSameAs(notes);
    }

    [Fact]
    public void WhereAuthorizedVia_NullablePointer_ExcludesRowsWithoutAnOwner() {
        var docs = Docs().ToList();
        var notes = new[] {
            new LooseNote(Guid.NewGuid(), docs[0].Id),
            new LooseNote(Guid.NewGuid(), null),
            new LooseNote(Guid.NewGuid(), docs[1].Id)
        }.AsQueryable();

        notes.WhereAuthorizedVia(
                n => n.DocId, docs.AsQueryable(), d => (Guid?)d.Id,
                new FakeQueryAuthorizer<Doc>(d => d.OwnerId == OwnerA), UserA)
            .Select(n => n.DocId).ToList().Should().Equal(docs[0].Id);
    }

    [Fact]
    public void Combinators_RefuseNullAuthorizers_AndIgnoreLaterChangesToTheArray() {
        var ownedByA = new FakeQueryAuthorizer<Doc>(d => d.OwnerId == OwnerA);
        var nullAnyOf = () => QueryAuthorizers.AnyOf<Doc>(ownedByA, null!);
        var nullAllOf = () => QueryAuthorizers.AllOf<Doc>(null!, ownedByA);
        nullAnyOf.Should().Throw<ArgumentException>();
        nullAllOf.Should().Throw<ArgumentException>();

        IQueryAuthorizer<Doc>[] parts = [ownedByA];
        var composed = QueryAuthorizers.AllOf(parts);
        parts[0] = new FakeQueryAuthorizer<Doc>(null);

        Docs().WhereAuthorized(composed, UserA).ToList().Should().HaveCount(2, "the composition kept its own copy");
    }

    [Fact]
    public void AnyOf_OrsTheFilters_AndAnUnrestrictedOneLiftsTheRestriction() {
        var ownedByA = new FakeQueryAuthorizer<Doc>(d => d.OwnerId == OwnerA);
        var ownedByB = QueryAuthorizers.From<Doc>((_, _) => d => d.OwnerId == OwnerB);

        Docs().WhereAuthorized(QueryAuthorizers.AnyOf<Doc>(ownedByA, ownedByB), UserA).ToList().Should().HaveCount(3);
        Docs().WhereAuthorized(QueryAuthorizers.AnyOf<Doc>(ownedByA), UserA).ToList().Should().HaveCount(2);
        QueryAuthorizers.AnyOf<Doc>(ownedByA, new FakeQueryAuthorizer<Doc>(null)).GetFilter(UserA, ResourceOperation.Read)
            .Should().BeNull();
        Docs().WhereAuthorized(QueryAuthorizers.AnyOf<Doc>(), UserA).ToList().Should().BeEmpty("no authorizer grants anything");
    }

    [Fact]
    public void AllOf_AndsTheFilters_AndAnUnrestrictedOneAddsNothing() {
        var ownedByA = new FakeQueryAuthorizer<Doc>(d => d.OwnerId == OwnerA);
        var docs = Docs().ToList();
        var first = docs[0].Id;
        var onlyFirst = QueryAuthorizers.From<Doc>((_, _) => d => d.Id == first);

        docs.AsQueryable().WhereAuthorized(QueryAuthorizers.AllOf<Doc>(ownedByA, onlyFirst), UserA)
            .Select(d => d.Id).ToList().Should().Equal(first);
        docs.AsQueryable().WhereAuthorized(QueryAuthorizers.AllOf<Doc>(ownedByA, new FakeQueryAuthorizer<Doc>(null)), UserA)
            .ToList().Should().HaveCount(2);
        // An empty AND would lift every restriction: refused rather than failing open.
        var empty = () => QueryAuthorizers.AllOf<Doc>();
        empty.Should().Throw<ArgumentException>();
    }

    private sealed record Note(Guid Id, Guid DocId);

    private sealed record LooseNote(Guid Id, Guid? DocId);

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
