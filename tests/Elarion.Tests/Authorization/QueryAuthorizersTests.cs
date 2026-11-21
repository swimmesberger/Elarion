using System.Linq.Expressions;
using AwesomeAssertions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Xunit;

namespace Elarion.Tests.Authorization;

public sealed class QueryAuthorizersTests {
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Doc[] Docs = [
        new(1, Alice, IsRestricted: false, Reviewer: Bob),
        new(2, Alice, IsRestricted: true, Reviewer: null),
        new(3, Bob, IsRestricted: false, Reviewer: null),
        new(4, Bob, IsRestricted: true, Reviewer: Alice)
    ];

    private static readonly ICurrentUser Anonymous = new FakeCurrentUser { IsAuthenticated = false };

    private static FakeCurrentUser User(Guid id, string[]? roles = null, params string[] permissions) {
        return new FakeCurrentUser {
            IsAuthenticated = true,
            UserId = id.ToString(),
            Roles = roles ?? [],
            Claims = permissions.Select(permission => ("permission", permission)).ToArray()
        };
    }

    private static int[] Visible(IQueryAuthorizer<Doc> authorizer, ICurrentUser user, ResourceOperation? operation = null) {
        return Docs.AsQueryable().WhereAuthorized(authorizer, user, operation).Select(doc => doc.Id).ToArray();
    }

    [Fact]
    public void Rows_AreVisibleToAuthenticatedCallersOnly() {
        var unrestricted = QueryAuthorizers.Rows<Doc>(doc => !doc.IsRestricted);

        Visible(unrestricted, User(Alice)).Should().Equal(1, 3);
        Visible(unrestricted, Anonymous).Should().BeEmpty("a building block never serves an unauthenticated caller");
    }

    [Fact]
    public void Unrestricted_LiftsTheFilterForAuthenticatedCallersOnly() {
        var everything = QueryAuthorizers.Unrestricted<Doc>();

        everything.GetFilter(User(Alice), ResourceOperation.Read).Should().BeNull();
        Visible(everything, Anonymous).Should().BeEmpty();
    }

    [Fact]
    public void ForUser_RelatesRowsToTheCallersTypedId() {
        var owned = QueryAuthorizers.ForUser<Doc, Guid>((doc, userId) => doc.Owner == userId);

        Visible(owned, User(Alice)).Should().Equal(1, 2);
        Visible(owned, User(Bob)).Should().Equal(3, 4);
    }

    [Fact]
    public void ForUser_DeniesACallerWhoIsNotAuthenticatedOrWhoseIdDoesNotParse() {
        var owned = QueryAuthorizers.ForUser<Doc, Guid>((doc, userId) => doc.Owner == userId);
        var signedOutWithAnId = new FakeCurrentUser { IsAuthenticated = false, UserId = Alice.ToString() };
        var notAGuid = new FakeCurrentUser { IsAuthenticated = true, UserId = "service-account" };

        Visible(owned, signedOutWithAnId).Should().BeEmpty();
        Visible(owned, notAGuid).Should().BeEmpty();
    }

    [Fact]
    public void ForUser_PassesTheIdAsACapturedValue_NotAsALiteral() {
        var owned = QueryAuthorizers.ForUser<Doc, Guid>((doc, userId) => doc.Owner == userId);

        var filter = owned.GetFilter(User(Alice), ResourceOperation.Read)!;

        // A closure member access is what a query provider turns into a parameter; a constant would be inlined.
        var comparison = (BinaryExpression)filter.Body;
        comparison.Right.Should().BeAssignableTo<MemberExpression>();
        filter.Parameters.Should().ContainSingle();
    }

    [Fact]
    public void ForUser_SupportsStringAndIntegerIds() {
        var byName = QueryAuthorizers.ForUser<Named, string>((row, userId) => row.Owner == userId);
        var byNumber = QueryAuthorizers.ForUser<Numbered, long>((row, userId) => row.Owner == userId);
        Named[] named = [new("alice"), new("bob")];
        Numbered[] numbered = [new(7), new(8)];

        named.AsQueryable()
            .WhereAuthorized(byName, new FakeCurrentUser { IsAuthenticated = true, UserId = "alice" })
            .Should().Equal(named[0]);
        numbered.AsQueryable()
            .WhereAuthorized(byNumber, new FakeCurrentUser { IsAuthenticated = true, UserId = "8" })
            .Should().Equal(numbered[1]);
    }

    [Fact]
    public void WhenPermission_GrantsItsRowsToHoldersOnly() {
        var readers = QueryAuthorizers.WhenPermission(
            "docs.read", QueryAuthorizers.Rows<Doc>(doc => !doc.IsRestricted));

        Visible(readers, User(Alice, permissions: "docs.read")).Should().Equal(1, 3);
        Visible(readers, User(Alice, permissions: "docs.write")).Should().BeEmpty();
        Visible(readers, Anonymous).Should().BeEmpty();
    }

    [Fact]
    public void WhenPermission_ReadsTheConfiguredClaimType() {
        var readers = QueryAuthorizers.WhenPermission("docs.read", QueryAuthorizers.Unrestricted<Doc>(), "perm");
        var holder = new FakeCurrentUser {
            IsAuthenticated = true, UserId = Alice.ToString(), Claims = [("perm", "docs.read")]
        };

        readers.GetFilter(holder, ResourceOperation.Read).Should().BeNull();
        Visible(readers, User(Alice, permissions: "docs.read")).Should().BeEmpty("the default claim type is not read");
    }

    [Fact]
    public void WhenRole_GrantsItsRowsToMembersOnly() {
        var administrators = QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Doc>());

        administrators.GetFilter(User(Alice, ["Admin"]), ResourceOperation.Read).Should().BeNull();
        Visible(administrators, User(Alice, ["Staff"])).Should().BeEmpty();
    }

    [Fact]
    public void When_DoesNotAskTheConditionOfAnUnauthenticatedCaller() {
        var asked = false;
        var gated = QueryAuthorizers.When(
            _ => {
                asked = true;
                return true;
            },
            QueryAuthorizers.Unrestricted<Doc>());

        Visible(gated, Anonymous).Should().BeEmpty();
        asked.Should().BeFalse();
    }

    [Fact]
    public void ForOperations_DeniesEveryOperationItDoesNotList() {
        var readOnly = QueryAuthorizers.ForOperations(
            QueryAuthorizers.Rows<Doc>(doc => !doc.IsRestricted), ResourceOperation.Read);

        Visible(readOnly, User(Alice), ResourceOperation.Read).Should().Equal(1, 3);
        Visible(readOnly, User(Alice), ResourceOperation.Update).Should().BeEmpty();
        Visible(readOnly, User(Alice), new ResourceOperation("export")).Should().BeEmpty("an unknown operation is denied");
    }

    [Fact]
    public void ForOperations_RefusesAnEmptyOrInvalidOperationList() {
        var rows = QueryAuthorizers.Rows<Doc>(doc => true);
        var empty = () => QueryAuthorizers.ForOperations(rows);
        var invalid = () => QueryAuthorizers.ForOperations(rows, default(ResourceOperation));

        empty.Should().Throw<ArgumentException>();
        invalid.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComposedRule_IsWiderForReadingThanForWriting() {
        // Everyone who may read sees the unrestricted rows; only the owner changes a row.
        var rule = QueryAuthorizers.AnyOf(
            QueryAuthorizers.ForOperations(
                QueryAuthorizers.WhenPermission("docs.read", QueryAuthorizers.Rows<Doc>(doc => !doc.IsRestricted)),
                ResourceOperation.Read),
            QueryAuthorizers.ForUser<Doc, Guid>((doc, userId) => doc.Owner == userId));
        var alice = User(Alice, permissions: "docs.read");

        Visible(rule, alice, ResourceOperation.Read).Should().Equal(1, 2, 3);
        Visible(rule, alice, ResourceOperation.Update).Should().Equal(1, 2);
    }

    [Fact]
    public void ComposedRule_OfTheReviewedShape_MatchesEachCaller() {
        var rule = QueryAuthorizers.AnyOf(
            QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Doc>()),
            QueryAuthorizers.WhenPermission("docs.read", QueryAuthorizers.Rows<Doc>(doc => !doc.IsRestricted)),
            QueryAuthorizers.ForUser<Doc, Guid>((doc, userId) => doc.Owner == userId),
            QueryAuthorizers.ForUser<Doc, Guid>((doc, userId) => doc.Reviewer == userId));

        rule.GetFilter(User(Bob, ["Admin"]), ResourceOperation.Read).Should().BeNull();
        Visible(rule, User(Alice, permissions: "docs.read")).Should().Equal(1, 2, 3, 4);
        Visible(rule, User(Alice)).Should().Equal(1, 2, 4);
        Visible(rule, User(Bob)).Should().Equal(1, 3, 4);
        Visible(rule, Anonymous).Should().BeEmpty();
    }

    [Fact]
    public void AnyOf_LeavesOutAlternativesThatDenyEveryone() {
        var owned = QueryAuthorizers.ForUser<Doc, Guid>((doc, userId) => doc.Owner == userId);
        var rule = QueryAuthorizers.AnyOf(
            QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Doc>()), owned);

        var filter = rule.GetFilter(User(Alice), ResourceOperation.Read)!;

        // The administrator alternative denies this caller, so only the owner comparison reaches the query.
        filter.Body.NodeType.Should().Be(ExpressionType.Equal);
    }

    [Fact]
    public void AllOf_KeepsAScopeAroundAnUnrestrictedAlternative_WhereAnyOfWouldLiftIt() {
        var scope = QueryAuthorizers.Rows<Doc>(doc => !doc.IsRestricted);
        var administrators = QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Doc>());
        var owned = QueryAuthorizers.ForUser<Doc, Guid>((doc, userId) => doc.Owner == userId);
        var admin = User(Bob, ["Admin"]);

        Visible(QueryAuthorizers.AllOf(scope, QueryAuthorizers.AnyOf(administrators, owned)), admin)
            .Should().Equal([1, 3], "the scope stays outside the alternatives");
        QueryAuthorizers.AnyOf(scope, administrators, owned).GetFilter(admin, ResourceOperation.Read)
            .Should().BeNull("an unrestricted alternative lifts everything it is OR-ed with");
    }

    [Fact]
    public void AllOf_DeniesEveryoneAsSoonAsOnePartDoes() {
        var rule = QueryAuthorizers.AllOf(
            QueryAuthorizers.Unrestricted<Doc>(),
            QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Doc>()));

        Visible(rule, User(Alice)).Should().BeEmpty();
        rule.GetFilter(User(Alice, ["Admin"]), ResourceOperation.Read).Should().BeNull();
    }

    [Fact]
    public void WhereNotAuthorized_IsTheExactComplement() {
        var rule = QueryAuthorizers.AnyOf(
            QueryAuthorizers.WhenPermission("docs.read", QueryAuthorizers.Rows<Doc>(doc => !doc.IsRestricted)),
            QueryAuthorizers.ForUser<Doc, Guid>((doc, userId) => doc.Reviewer == userId));
        var alice = User(Alice, permissions: "docs.read");

        var visible = Visible(rule, alice);
        var hidden = Docs.AsQueryable().WhereNotAuthorized(rule, alice).Select(doc => doc.Id).ToArray();

        visible.Should().Equal(1, 3, 4);
        hidden.Should().Equal([2], "a row without a reviewer is hidden, not left out of both sets");
        visible.Concat(hidden).Should().BeEquivalentTo(Docs.Select(doc => doc.Id));
    }

    [Fact]
    public void WhereNotAuthorized_HidesNothingFromAnUnrestrictedCaller_AndEverythingFromADeniedOne() {
        var administrators = QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Doc>());
        var source = Docs.AsQueryable();

        source.WhereNotAuthorized(administrators, User(Alice, ["Admin"])).Should().BeEmpty();
        source.WhereNotAuthorized(administrators, User(Alice)).Should().BeSameAs(source);
        source.WhereNotAuthorized(administrators, Anonymous).Should().BeSameAs(source);
    }

    [Fact]
    public void WhereNotAuthorized_PassesTheOperation() {
        var readOnly = QueryAuthorizers.ForOperations(QueryAuthorizers.Unrestricted<Doc>(), ResourceOperation.Read);
        var source = Docs.AsQueryable();

        source.WhereNotAuthorized(readOnly, User(Alice)).Should().BeEmpty();
        source.WhereNotAuthorized(readOnly, User(Alice), ResourceOperation.Delete).Should().HaveCount(Docs.Length);
    }

    [Fact]
    public void BuildingBlocks_RefuseNullArguments() {
        var rows = QueryAuthorizers.Rows<Doc>(doc => true);
        Action[] calls = [
            () => QueryAuthorizers.Rows<Doc>(null!),
            () => QueryAuthorizers.ForUser<Doc, Guid>(null!),
            () => QueryAuthorizers.When(null!, rows),
            () => QueryAuthorizers.When<Doc>(_ => true, null!),
            () => QueryAuthorizers.WhenPermission("", rows),
            () => QueryAuthorizers.WhenPermission("docs.read", rows, ""),
            () => QueryAuthorizers.WhenRole("", rows),
            () => QueryAuthorizers.ForOperations<Doc>(null!, ResourceOperation.Read)
        ];

        foreach (var call in calls) call.Should().Throw<ArgumentException>();
    }

    private sealed record Doc(int Id, Guid Owner, bool IsRestricted, Guid? Reviewer);

    private sealed record Named(string Owner);

    private sealed record Numbered(long Owner);
}
