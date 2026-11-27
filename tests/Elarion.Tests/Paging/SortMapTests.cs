using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Paging;
using Xunit;

namespace Elarion.Tests.Paging;

public sealed class SortMapTests {
    private sealed record Row(int Id, string Name);

    private static readonly Row[] Rows = [
        new(1, "charlie"),
        new(2, "alice"),
        new(3, "bob")
    ];

    [Fact]
    public void Apply_NoSort_UsesDefaultKeyAscending() {
        var map = SortMap<Row>.CreateBuilder("id", r => r.Id).Add("name", r => r.Name).Build();

        var ids = map.Apply(Rows.AsQueryable(), null).Select(r => r.Id).ToArray();

        ids.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Apply_NamedKeyDescending_SortsByThatKey() {
        var map = SortMap<Row>.CreateBuilder("id", r => r.Id).Add("name", r => r.Name).Build();

        var names = map.Apply(Rows.AsQueryable(), "-name").Select(r => r.Name).ToArray();

        names.Should().Equal("charlie", "bob", "alice");
    }

    [Fact]
    public void Apply_KeyIsCaseInsensitive() {
        var map = SortMap<Row>.CreateBuilder("id", r => r.Id).Add("name", r => r.Name).Build();

        var names = map.Apply(Rows.AsQueryable(), "-NAME").Select(r => r.Name).ToArray();

        names.Should().Equal("charlie", "bob", "alice");
    }

    [Fact]
    public void Apply_UnknownKey_FallsBackToDefaultAscending() {
        var map = SortMap<Row>.CreateBuilder("id", r => r.Id).Add("name", r => r.Name).Build();

        var ids = map.Apply(Rows.AsQueryable(), "-unknown").Select(r => r.Id).ToArray();

        ids.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Build_SnapshotsBuilder_LaterMutationDoesNotLeak() {
        var builder = SortMap<Row>.CreateBuilder("id", r => r.Id);
        var map = builder.Build();
        builder.Add("name", r => r.Name);

        var ids = map.Apply(Rows.AsQueryable(), "name").Select(r => r.Id).ToArray();

        ids.Should().Equal(1, 2, 3);
    }

    private sealed record RankedRow(int Id, int Rank);

    private static readonly RankedRow[] RankedRows = [
        new(1, 5),
        new(2, 5),
        new(3, 1)
    ];

    [Fact]
    public void Apply_DeclaredDescendingDefault_OrdersDescending() {
        var map = SortMap<RankedRow>.CreateBuilder("id", r => r.Id, SortDirection.Descending).Build();

        var ids = map.Apply(RankedRows.AsQueryable(), null).Select(r => r.Id).ToArray();

        ids.Should().Equal(3, 2, 1);
    }

    [Fact]
    public void Apply_PlusPrefix_OverridesDeclaredDescendingToAscending() {
        var map = SortMap<RankedRow>.CreateBuilder("id", r => r.Id, SortDirection.Descending).Build();

        var ids = map.Apply(RankedRows.AsQueryable(), "+id").Select(r => r.Id).ToArray();

        ids.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Apply_CompositeTiebreaker_BreaksTiesByTrailingColumn() {
        var map = SortMap<RankedRow>.CreateBuilder("rank", r => r.Rank).ThenBy(r => r.Id).Build();

        var ids = map.Apply(RankedRows.AsQueryable(), "rank").Select(r => r.Id).ToArray();

        // Rank ascending puts row 3 (rank 1) first; the rank-5 tie is broken by Id ascending.
        ids.Should().Equal(3, 1, 2);
    }

    [Fact]
    public void Apply_MinusPrefix_FlipsPrimaryButLeavesTiebreakerFixed() {
        var map = SortMap<RankedRow>.CreateBuilder("rank", r => r.Rank).ThenBy(r => r.Id).Build();

        var ids = map.Apply(RankedRows.AsQueryable(), "-rank").Select(r => r.Id).ToArray();

        // Rank descending puts the rank-5 group first; its tiebreaker stays Id ascending (1, 2), then rank 1.
        ids.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Apply_BareKey_UsesTheKeysDeclaredDirection() {
        // A bare key is not "ascending": it is the key's declared direction. Ascending on a descending key is "+key".
        var map = SortMap<RankedRow>.CreateBuilder("id", r => r.Id)
            .Add("rank", r => r.Rank, SortDirection.Descending).ThenBy(r => r.Id)
            .Build();

        map.Apply(RankedRows.AsQueryable(), "rank").Select(r => r.Id).ToArray().Should().Equal(1, 2, 3);
        map.Apply(RankedRows.AsQueryable(), "+rank").Select(r => r.Id).ToArray().Should().Equal(3, 1, 2);
    }

    [Fact]
    public void Keys_ListEachKeyInDeclarationOrderWithItsDeclaredDirection() {
        var map = SortMap<Row>.CreateBuilder("id", r => r.Id, SortDirection.Descending)
            .Add("name", r => r.Name)
            .Build();

        map.Keys.Should().Equal(
            new SortKey("id", SortDirection.Descending, true),
            new SortKey("name", SortDirection.Ascending, false));
    }

    [Fact]
    public void Keys_ReAddedKeyKeepsItsPositionWithTheLastDeclaration() {
        var map = SortMap<Row>.CreateBuilder("id", r => r.Id)
            .Add("name", r => r.Name)
            .Add("id", r => r.Id, SortDirection.Descending)
            .Build();

        map.Keys.Should().Equal(
            new SortKey("id", SortDirection.Descending, true),
            new SortKey("name", SortDirection.Ascending, false));
    }

    [Fact]
    public void Validate_KnownPrefixedOrBlankKey_Passes() {
        var map = SortMap<Row>.CreateBuilder("id", r => r.Id).Add("name", r => r.Name).Build();

        map.Validate("-NAME").Should().BeNull();
        map.Validate("+id").Should().BeNull();
        map.Validate("  ").Should().BeNull();
        map.Validate(null).Should().BeNull();
    }

    [Fact]
    public void Validate_UnknownKey_FailsWithAFieldErrorNamingTheAllowedKeys() {
        var map = SortMap<Row>.CreateBuilder("id", r => r.Id).Add("name", r => r.Name).Build();

        var error = map.Validate("-unknown", "orderBy");

        error!.Kind.Should().Be(ErrorKind.Validation);
        error.Code.Should().Be(PagingErrorCodes.SortKeyUnknown);
        var data = error.Data.Should().BeOfType<ValidationErrorData>().Subject;
        data.FieldErrors!["orderBy"].Should().ContainSingle()
            .Which.Should().Be("Unknown sort key 'unknown'. Allowed keys: id, name.");
    }

    private sealed record Contact(int Id, string City, string Name);

    private static readonly Contact[] Contacts = [
        new(1, "Linz", "Bauer"),
        new(2, "Graz", "Huber"),
        new(3, "Linz", "Auer"),
        new(4, "Graz", "Huber"),
        new(5, "Wien", "Auer")
    ];

    private static readonly SortMap<Contact> ContactSort = SortMap<Contact>
        .CreateBuilder("id", c => c.Id)
        .Add("city", c => c.City)
        .ThenBy(c => c.Id, SortDirection.Descending)
        .Add("name", c => c.Name, SortDirection.Descending)
        .ThenBy(c => c.Id)
        .Build();

    [Fact]
    public void Apply_SeveralKeys_OrderLeftToRight_ThenTheLastKeysTiebreakers() {
        var ids = ContactSort.Apply(Contacts.AsQueryable(), "+city,-name").Select(c => c.Id).ToArray();

        // Graz (Huber 2, Huber 4: tie broken by name's tiebreaker, Id ascending), Linz (Bauer, Auer), Wien.
        ids.Should().Equal(2, 4, 1, 3, 5);
    }

    [Fact]
    public void Apply_SeveralKeys_BareKeyKeepsItsDeclaredDirection_AndSpacesAreIgnored() {
        // A bare "name" is descending (its declaration); "+name" is ascending.
        ContactSort.Apply(Contacts.AsQueryable(), " city , name ").Select(c => c.Id).ToArray()
            .Should().Equal(2, 4, 1, 3, 5);
        ContactSort.Apply(Contacts.AsQueryable(), "-city,+name").Select(c => c.Id).ToArray()
            .Should().Equal(5, 3, 1, 2, 4);
    }

    [Fact]
    public void Apply_SingleKey_KeepsItsOwnTiebreakers() {
        // city's own tiebreaker is Id descending: Graz 4, 2; Linz 3, 1; Wien 5.
        ContactSort.Apply(Contacts.AsQueryable(), "city").Select(c => c.Id).ToArray().Should().Equal(4, 2, 3, 1, 5);
    }

    [Theory]
    [InlineData("city,city")]
    [InlineData("city,-CITY")]
    [InlineData("city,unknown")]
    [InlineData("city,")]
    [InlineData("city,name,id,city")]
    public void Apply_RefusedSort_FallsBackToTheDefault(string sort) {
        ContactSort.Apply(Contacts.AsQueryable(), sort).Select(c => c.Id).ToArray().Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public void Validate_SeveralKnownKeys_Pass() {
        ContactSort.Validate("+city,-name").Should().BeNull();
        ContactSort.Validate("city,name,id").Should().BeNull();
    }

    [Theory]
    [InlineData("city,-City", "Sort key 'City' is repeated. Allowed keys: id, city, name.")]
    [InlineData("city,zip", "Unknown sort key 'zip'. Allowed keys: id, city, name.")]
    [InlineData("city,", "Unknown sort key ''. Allowed keys: id, city, name.")]
    [InlineData("city,name,id,city", "A sort combines at most 3 keys. Allowed keys: id, city, name.")]
    public void Validate_RefusedSort_FailsWithTheSortCode(string sort, string expected) {
        var error = ContactSort.Validate(sort);

        error!.Code.Should().Be(PagingErrorCodes.SortKeyUnknown);
        error.Message.Should().Be(expected);
        error.Data.Should().BeOfType<ValidationErrorData>().Subject.FieldErrors!["sort"].Should().Equal(expected);
    }

    [Fact]
    public void Validate_MessageHook_FormatsTheRefusal() {
        var error = ContactSort.Validate("plz", "orderBy", refusal =>
            $"Unbekannter Sortierschlüssel „{refusal.Key}“ ({refusal.Reason}, {refusal.AllowedKeys.Count} erlaubt).");

        const string expected = "Unbekannter Sortierschlüssel „plz“ (UnknownKey, 3 erlaubt).";
        error!.Message.Should().Be(expected);
        error.Data.Should().BeOfType<ValidationErrorData>().Subject.FieldErrors!["orderBy"].Should().Equal(expected);
    }

    [Fact]
    public void MaxLevels_One_AllowsASingleKeyOnly() {
        var map = SortMap<Contact>.CreateBuilder("id", c => c.Id).Add("city", c => c.City).MaxLevels(1).Build();

        map.MaxLevels.Should().Be(1);
        map.Validate("city").Should().BeNull();
        map.Validate("city,id")!.Code.Should().Be(PagingErrorCodes.SortKeyUnknown);
        ContactSort.MaxLevels.Should().Be(SortMap<Contact>.DefaultMaxLevels);
    }

    [Fact]
    public void MaxLevels_BelowOne_Throws() {
        var act = () => SortMap<Contact>.CreateBuilder("id", c => c.Id).MaxLevels(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_HostileSortOfManyKeys_IsRefusedWithoutLookingUpEveryKey() {
        // 200,000 keys in one parameter: refused on the level count, in one pass over the text.
        var hostile = string.Join(',', Enumerable.Repeat("city", 200_000));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = ContactSort.Validate(hostile);
        var ids = ContactSort.Apply(Contacts.AsQueryable(), hostile).Select(c => c.Id).ToArray();
        watch.Stop();

        error!.Message.Should().StartWith("A sort combines at most 3 keys.");
        ids.Should().Equal(1, 2, 3, 4, 5);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}
