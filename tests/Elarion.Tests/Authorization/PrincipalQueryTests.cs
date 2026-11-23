using System.Linq.Expressions;
using AwesomeAssertions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Xunit;

namespace Elarion.Tests.Authorization;

/// <summary>
/// The inverse direction of a resource rule: <c>WhereAuthorizedFor</c> lists the principals a row is visible to from the
/// same rule <c>WhereAuthorized</c> filters rows with, and refuses a rule it could only answer in part.
/// </summary>
public sealed class PrincipalQueryTests {
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Carol = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Dave = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Erin = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static readonly Doc[] Docs = [
        new(1, Alice, IsRestricted: false, Reviewer: Bob),
        new(2, Alice, IsRestricted: true, Reviewer: null),
        new(3, Bob, IsRestricted: false, Reviewer: null),
        new(4, Bob, IsRestricted: true, Reviewer: Erin)
    ];

    private static readonly FakeCurrentUser[] Users = [
        User(Alice),
        User(Bob, permissions: "docs.read"),
        User(Carol, permissions: ["docs.read", "docs.write"]),
        User(Dave, ["Admin"]),
        User(Erin, ["Staff"])
    ];

    private static readonly IPrincipalDirectory<Guid> Directory = new UserDirectory(Users);

    private static readonly IQueryable<Guid> Everyone = Users.Select(user => Guid.Parse(user.UserId)).AsQueryable();

    private static readonly IQueryAuthorizer<Doc> Owner =
        QueryAuthorizers.ForUser<Doc, Guid>((doc, userId) => doc.Owner == userId);

    private static readonly IQueryAuthorizer<Doc> Rule = QueryAuthorizers.AnyOf(
        QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Doc>()),
        QueryAuthorizers.ForOperations(
            QueryAuthorizers.WhenPermission("docs.read", QueryAuthorizers.Rows<Doc>(doc => !doc.IsRestricted)),
            ResourceOperation.Read),
        QueryAuthorizers.WhenPermission("docs.write", QueryAuthorizers.Rows<Doc>(doc => !doc.IsRestricted)),
        Owner,
        QueryAuthorizers.ForUser<Doc, Guid>((doc, userId) => doc.Reviewer == userId));

    private static FakeCurrentUser User(Guid id, string[]? roles = null, params string[] permissions) {
        return new FakeCurrentUser {
            IsAuthenticated = true,
            UserId = id.ToString(),
            Roles = roles ?? [],
            Claims = permissions.Select(permission => ("permission", permission)).ToArray()
        };
    }

    private static Guid[] Readers(
        IQueryAuthorizer<Doc> rule, Doc doc, ResourceOperation? operation = null, IQueryable<Guid>? candidates = null) {
        return (candidates ?? Everyone)
            .WhereAuthorizedFor(new[] { doc }.AsQueryable(), rule, Directory, operation)
            .ToArray();
    }

    [Theory]
    [InlineData("read")]
    [InlineData("update")]
    [InlineData("export")]
    public void Inverse_AgreesWithTheForwardFilter_ForEveryRowAndPrincipal(string operationName) {
        var operation = new ResourceOperation(operationName);

        foreach (var doc in Docs) {
            var forward = Users
                .Where(user => Docs.AsQueryable().WhereAuthorized(Rule, user, operation).Contains(doc))
                .Select(user => Guid.Parse(user.UserId));

            Readers(Rule, doc, operation).Should().BeEquivalentTo(forward, "both directions read one rule (doc {0})", doc.Id);
        }
    }

    [Fact]
    public void Inverse_OfTheReviewedShape_ListsEachRowsReaders() {
        Readers(Rule, Docs[0]).Should().BeEquivalentTo([Alice, Bob, Carol, Dave]);
        Readers(Rule, Docs[1]).Should().BeEquivalentTo([Alice, Dave]);
        Readers(Rule, Docs[3]).Should().BeEquivalentTo([Bob, Dave, Erin]);
        // Reading is wider than writing: docs.read alone does not admit to an update.
        Readers(Rule, Docs[0], ResourceOperation.Update).Should().BeEquivalentTo([Alice, Bob, Carol, Dave]);
        Readers(Rule, Docs[2], ResourceOperation.Update).Should().BeEquivalentTo([Bob, Carol, Dave]);
        Readers(Rule, Docs[2], ResourceOperation.Read).Should().BeEquivalentTo([Bob, Carol, Dave]);
    }

    [Fact]
    public void Inverse_ReturnsOnlyCandidates() {
        var staff = new[] { Bob, Erin }.AsQueryable();

        Readers(Rule, Docs[0], candidates: staff).Should().Equal(Bob);
        Readers(Rule, Docs[0], candidates: Array.Empty<Guid>().AsQueryable()).Should().BeEmpty();
    }

    [Fact]
    public void Inverse_OverSeveralRows_ListsWhoMayAccessAtLeastOne_AndNobodyForNoRows() {
        Everyone.WhereAuthorizedFor(Docs.AsQueryable(), Owner, Directory).Should().BeEquivalentTo([Alice, Bob]);
        Everyone.WhereAuthorizedFor(Array.Empty<Doc>().AsQueryable(), QueryAuthorizers.Unrestricted<Doc>(), Directory)
            .Should().BeEmpty("an unrestricted rule still needs a row to be asked about");
        Everyone.WhereAuthorizedFor(Docs.AsQueryable(), QueryAuthorizers.Unrestricted<Doc>(), Directory)
            .Should().BeEquivalentTo(Everyone);
    }

    [Fact]
    public void InverseForAll_ListsWhoMayAccessEveryRow_TheIntersectionOfEachRowsReaders() {
        // Every non-empty selection of the docs, by bit mask.
        for (var mask = 1; mask < 1 << Docs.Length; mask++) {
            var selection = Docs.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
            var expected = selection
                .Select(doc => (IEnumerable<Guid>)Readers(Rule, doc))
                .Aggregate((left, right) => left.Intersect(right));

            Everyone.WhereAuthorizedForAll(selection.AsQueryable(), Rule, Directory)
                .Should().BeEquivalentTo(expected, "selection {0}", string.Join(",", selection.Select(doc => doc.Id)));
        }
    }

    [Fact]
    public void InverseForAll_OfTheReviewedShape_KeepsOnlyWhoSeesTheWholeSelection() {
        Everyone.WhereAuthorizedForAll(Docs.AsQueryable(), Rule, Directory).Should().Equal(Dave);
        Everyone.WhereAuthorizedForAll(new[] { Docs[0], Docs[2] }.AsQueryable(), Rule, Directory)
            .Should().BeEquivalentTo([Bob, Carol, Dave], "Alice owns doc 1 only; Bob and Carol read both");
        Everyone.WhereAuthorizedForAll(new[] { Docs[1], Docs[2] }.AsQueryable(), Rule, Directory)
            .Should().Equal([Dave], "only the administrator reads Alice's restricted doc and Bob's open one");
    }

    [Fact]
    public void InverseForAll_OfNoRows_IsNobody() {
        Everyone.WhereAuthorizedForAll(Array.Empty<Doc>().AsQueryable(), QueryAuthorizers.Unrestricted<Doc>(), Directory)
            .Should().BeEmpty("every row of an empty selection would be everyone; nobody is the fail-closed answer");
    }

    [Fact]
    public void InverseForAll_IsRefused_WhenAnyPartOfTheRuleIsOpaque() {
        var rule = QueryAuthorizers.AnyOf(Owner, QueryAuthorizers.From<Doc>((_, _) => null));

        var ask = () => Everyone.WhereAuthorizedForAll(Docs.AsQueryable(), rule, Directory).ToArray();

        ask.Should().Throw<NotSupportedException>().WithMessage("*'QueryAuthorizers.From'*");
    }

    [Fact]
    public void UnlessClaim_WithdrawsAnAlternative_FromASessionCarryingTheClaim() {
        var rule = QueryAuthorizers.AnyOf(
            QueryAuthorizers.UnlessClaim("act", QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Doc>())),
            Owner);
        var administrator = User(Dave, ["Admin"]);
        var viewingAsAlice = new FakeCurrentUser {
            IsAuthenticated = true, UserId = Dave.ToString(), Roles = ["Admin"], Claims = [("act", Alice.ToString())]
        };

        Docs.AsQueryable().WhereAuthorized(rule, administrator).Should().HaveCount(Docs.Length);
        Docs.AsQueryable().WhereAuthorized(rule, viewingAsAlice).Should().BeEmpty("the bypass is withdrawn");
        Docs.AsQueryable().WhereAuthorized(rule, new FakeCurrentUser { IsAuthenticated = false }).Should().BeEmpty();
    }

    [Fact]
    public void UnlessClaim_WithAValue_WithdrawsOnlyForThatValue() {
        var rule = QueryAuthorizers.UnlessClaim(
            "act", Alice.ToString(), QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Doc>()));
        FakeCurrentUser Acting(Guid subject) => new() {
            IsAuthenticated = true, UserId = Dave.ToString(), Roles = ["Admin"], Claims = [("act", subject.ToString())]
        };

        Docs.AsQueryable().WhereAuthorized(rule, Acting(Alice)).Should().BeEmpty();
        Docs.AsQueryable().WhereAuthorized(rule, Acting(Bob)).Should().HaveCount(Docs.Length);
    }

    [Fact]
    public void UnlessClaim_Inverse_LeavesOutWhomTheDirectoryListsWithTheClaim_AndAgreesWithTheFilter() {
        FakeCurrentUser[] users = [
            User(Alice),
            User(Bob, permissions: "docs.read"),
            new() {
                IsAuthenticated = true, UserId = Carol.ToString(),
                Claims = [("permission", "docs.read"), ("suspended", "true")]
            }
        ];
        var directory = new UserDirectory(users);
        var candidates = users.Select(user => Guid.Parse(user.UserId)).AsQueryable();
        var rule = QueryAuthorizers.AnyOf(
            QueryAuthorizers.UnlessClaim(
                "suspended",
                QueryAuthorizers.WhenPermission("docs.read", QueryAuthorizers.Rows<Doc>(doc => !doc.IsRestricted))),
            Owner);

        foreach (var doc in Docs) {
            var forward = users
                .Where(user => Docs.AsQueryable().WhereAuthorized(rule, user).Contains(doc))
                .Select(user => Guid.Parse(user.UserId));

            candidates.WhereAuthorizedFor(new[] { doc }.AsQueryable(), rule, directory)
                .Should().BeEquivalentTo(forward, "both directions read one rule (doc {0})", doc.Id);
        }

        candidates.WhereAuthorizedFor(new[] { Docs[0] }.AsQueryable(), rule, directory)
            .Should().BeEquivalentTo([Alice, Bob], "Carol holds the claim that withdraws her reading");
    }

    [Fact]
    public void Inverse_KeepsAScopeAroundAnUnrestrictedAlternative() {
        var scoped = QueryAuthorizers.AllOf(
            QueryAuthorizers.Rows<Doc>(doc => !doc.IsRestricted),
            QueryAuthorizers.AnyOf(QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Doc>()), Owner));

        Readers(scoped, Docs[0]).Should().BeEquivalentTo([Alice, Dave]);
        Readers(scoped, Docs[1]).Should().BeEmpty("the scope excludes the row for the administrator too");
    }

    [Fact]
    public void Inverse_ReadsTheConfiguredPermissionClaimType() {
        var rule = QueryAuthorizers.WhenPermission("docs.read", QueryAuthorizers.Unrestricted<Doc>(), "perm");

        Readers(rule, Docs[0]).Should().BeEmpty("the directory lists the permission under another claim type");
    }

    [Fact]
    public void Inverse_IsRefused_WhenAnyPartOfTheRuleIsOpaque() {
        var from = QueryAuthorizers.From<Doc>((_, _) => doc => doc.Id == 1);
        var when = QueryAuthorizers.When(user => user.IsInRole("Admin"), QueryAuthorizers.Unrestricted<Doc>());
        var custom = new HandWritten();
        (IQueryAuthorizer<Doc> Rule, string Named)[] opaque = [
            (from, "QueryAuthorizers.From"),
            (when, "QueryAuthorizers.When"),
            (custom, nameof(HandWritten)),
            // One opaque alternative refuses the whole rule: the owners alone would look like the complete answer.
            (QueryAuthorizers.AnyOf(Owner, from), "QueryAuthorizers.From"),
            (QueryAuthorizers.AllOf(Owner, custom), nameof(HandWritten)),
            (QueryAuthorizers.WhenRole("Admin", when), "QueryAuthorizers.When"),
            (QueryAuthorizers.ForOperations(QueryAuthorizers.AnyOf(custom), ResourceOperation.Read), nameof(HandWritten))
        ];

        foreach (var (rule, named) in opaque) {
            var ask = () => Readers(rule, Docs[0]);

            ask.Should().Throw<NotSupportedException>().WithMessage($"*'{named}'*");
        }
    }

    [Fact]
    public void Inverse_IsRefused_WhenTheRuleRelatesRowsToAnotherIdType() {
        var byNumber = QueryAuthorizers.ForUser<Doc, long>((doc, userId) => doc.Id == userId);

        var ask = () => Readers(byNumber, Docs[0]);

        ask.Should().Throw<NotSupportedException>().WithMessage("*Int64 ids*Guid ids*");
    }

    [Fact]
    public void Inverse_AcceptsAHandWrittenRelation() {
        var rule = QueryAuthorizers.AnyOf(Owner, new ReviewerRule());

        Readers(rule, Docs[0]).Should().BeEquivalentTo([Alice, Bob]);
    }

    private sealed record Doc(int Id, Guid Owner, bool IsRestricted, Guid? Reviewer);

    private sealed class HandWritten : IQueryAuthorizer<Doc> {
        public Expression<Func<Doc, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation) {
            return _ => false;
        }
    }

    private sealed class ReviewerRule : IQueryAuthorizer<Doc>, IPrincipalQueryAuthorizer<Doc> {
        public Expression<Func<Doc, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation) {
            return Guid.TryParse(user.UserId, out var id) ? doc => doc.Reviewer == id : _ => false;
        }

        public Expression<Func<Doc, TKey, bool>> GetPrincipalRelation<TKey>(
            IPrincipalDirectory<TKey> directory, ResourceOperation operation)
            where TKey : notnull {
            Expression<Func<Doc, Guid, bool>> relation = (doc, principal) => doc.Reviewer == principal;
            return relation as Expression<Func<Doc, TKey, bool>> ?? throw new NotSupportedException();
        }
    }

    /// <summary>The directory a principal's own roles and claims imply — agreement with the live principal by construction.</summary>
    private sealed class UserDirectory(IReadOnlyList<FakeCurrentUser> users) : IPrincipalDirectory<Guid> {
        public IQueryable<PrincipalRole<Guid>> Roles => users
            .SelectMany(user => user.Roles.Select(role =>
                new PrincipalRole<Guid> { PrincipalId = Guid.Parse(user.UserId), Role = role }))
            .AsQueryable();

        public IQueryable<PrincipalClaim<Guid>> Claims => users
            .SelectMany(user => user.Claims.Select(claim =>
                new PrincipalClaim<Guid> { PrincipalId = Guid.Parse(user.UserId), Type = claim.Type, Value = claim.Value }))
            .AsQueryable();
    }
}
