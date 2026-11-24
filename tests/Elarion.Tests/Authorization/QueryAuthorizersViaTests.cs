using AwesomeAssertions;
using Elarion.Abstractions.Authorization;
using Xunit;

namespace Elarion.Tests.Authorization;

/// <summary>
/// <c>QueryAuthorizers.Via</c>: a row is visible through an owner the caller may access, by the owner type's own rule,
/// in both directions — the filter for a caller and the relation <c>WhereAuthorizedFor</c> reads.
/// </summary>
public sealed class QueryAuthorizersViaTests {
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Carol = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Dave = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static readonly Contact AlicesOpen = new(Guid.Parse("a0000000-0000-0000-0000-000000000001"), Alice, false);
    private static readonly Contact AlicesRestricted = new(Guid.Parse("a0000000-0000-0000-0000-000000000002"), Alice, true);
    private static readonly Contact BobsOpen = new(Guid.Parse("a0000000-0000-0000-0000-000000000003"), Bob, false);

    private static readonly Contact[] Contacts = [AlicesOpen, AlicesRestricted, BobsOpen];

    private static readonly Email[] Emails = [
        Mail(1, AlicesOpen),
        Mail(2, AlicesRestricted),
        Mail(3, AlicesRestricted, BobsOpen),
        Mail(4),
        // Points at an invoice whose id equals a visible contact's: only the kind keeps it out.
        new(5, [], [new Link("invoice", AlicesOpen.Id)])
    ];

    private static readonly FakeCurrentUser[] Users = [
        User(Alice),
        User(Bob, permissions: "contacts.read"),
        User(Carol),
        User(Dave, roles: ["Admin"])
    ];

    private static readonly IPrincipalDirectory<Guid> Directory = new UserDirectory(Users);

    private static readonly IQueryable<Guid> Everyone = Users.Select(user => Guid.Parse(user.UserId)).AsQueryable();

    // The owner type's composed rule: administrators, readers of unrestricted contacts (reading only), the owner.
    private static readonly IQueryAuthorizer<Contact> ContactRule = QueryAuthorizers.AnyOf(
        QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Contact>()),
        QueryAuthorizers.ForOperations(
            QueryAuthorizers.WhenPermission("contacts.read", QueryAuthorizers.Rows<Contact>(c => !c.IsRestricted)),
            ResourceOperation.Read),
        QueryAuthorizers.ForUser<Contact, Guid>((contact, userId) => contact.OwnerId == userId));

    private static readonly IQueryAuthorizer<Email> ByNavigation =
        QueryAuthorizers.Via<Email, Contact>(email => email.Contacts, ContactRule);

    private static readonly IQueryAuthorizer<Email> ByPointer = QueryAuthorizers.Via<Email, Contact>(
        Contacts.AsQueryable(),
        (email, contact) => email.Links.Any(link => link.Kind == "contact" && link.RecordId == contact.Id),
        ContactRule);

    private static Email Mail(int id, params Contact[] contacts) {
        return new Email(id, contacts, contacts.Select(contact => new Link("contact", contact.Id)).ToArray());
    }

    private static FakeCurrentUser User(Guid id, string[]? roles = null, params string[] permissions) {
        return new FakeCurrentUser {
            IsAuthenticated = true,
            UserId = id.ToString(),
            Roles = roles ?? [],
            Claims = permissions.Select(permission => ("permission", permission)).ToArray()
        };
    }

    private static int[] Visible(IQueryAuthorizer<Email> rule, FakeCurrentUser user, ResourceOperation? operation = null) {
        return Emails.AsQueryable().WhereAuthorized(rule, user, operation).Select(email => email.Id).ToArray();
    }

    public static TheoryData<string> Forms => ["navigation", "pointer"];

    private static IQueryAuthorizer<Email> Form(string form) {
        return form == "navigation" ? ByNavigation : ByPointer;
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void Filter_AdmitsTheRowsWithAnOwnerTheCallerMayAccess(string form) {
        var rule = Form(form);

        Visible(rule, Users[0]).Should().Equal(1, 2, 3);
        Visible(rule, Users[1]).Should().Equal([1, 3], "Bob reads open contacts and owns one");
        Visible(rule, Users[2]).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void Filter_NeedsAnOwner_AlsoForACallerTheOwnerRuleDoesNotRestrict(string form) {
        Visible(Form(form), Users[3]).Should().Equal([1, 2, 3], "mail 4 has no owner, mail 5 points at an invoice");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void Filter_AsksTheOwnerAboutTheRowsOperation_OrTheOneGiven(string form) {
        var rule = Form(form);

        Visible(rule, Users[1], ResourceOperation.Update).Should().Equal([3], "reading open contacts is read-only");

        var readersMayUpdate = form == "navigation"
            ? QueryAuthorizers.Via<Email, Contact>(email => email.Contacts, ContactRule, ResourceOperation.Read)
            : QueryAuthorizers.Via<Email, Contact>(
                Contacts.AsQueryable(),
                (email, contact) => email.Links.Any(link => link.Kind == "contact" && link.RecordId == contact.Id),
                ContactRule,
                ResourceOperation.Read);
        Visible(readersMayUpdate, Users[1], ResourceOperation.Update).Should().Equal(1, 3);
    }

    [Fact]
    public void Filter_DeniesAnAnonymousCaller_EvenWhenTheOwnerRuleWouldNot() {
        var everyone = QueryAuthorizers.From<Contact>((_, _) => null);
        var rule = QueryAuthorizers.Via<Email, Contact>(email => email.Contacts, everyone);

        Visible(rule, new FakeCurrentUser { IsAuthenticated = false }).Should().BeEmpty();
        Visible(rule, Users[2]).Should().Equal([1, 2, 3], "the owner rule admits every authenticated caller");
    }

    [Fact]
    public void Filter_IsTheConstantFalse_WhenTheOwnerRuleDeniesTheCaller() {
        var administratorsOnly = QueryAuthorizers.Via<Email, Contact>(
            email => email.Contacts, QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted<Contact>()));

        var filter = administratorsOnly.GetFilter(Users[2], ResourceOperation.Read);

        // The deny-all shape, which an AnyOf leaves out of the SQL.
        filter!.Body.Should().BeOfType<System.Linq.Expressions.ConstantExpression>()
            .Which.Value.Should().Be(false);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void Inverse_AgreesWithTheFilter_ForEveryRowPrincipalAndOperation(string form) {
        var rule = Form(form);

        foreach (var operation in new[] { ResourceOperation.Read, ResourceOperation.Update }) {
            foreach (var email in Emails) {
                var forward = Users
                    .Where(user => Emails.AsQueryable().WhereAuthorized(rule, user, operation).Contains(email))
                    .Select(user => Guid.Parse(user.UserId));

                Everyone.WhereAuthorizedFor(new[] { email }.AsQueryable(), rule, Directory, operation)
                    .Should().BeEquivalentTo(forward, "both directions read one rule (mail {0}, {1})", email.Id,
                        operation.Name);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void Inverse_ListsWhoMayReadAnOwner_ForEachRow(string form) {
        var rule = Form(form);

        Readers(rule, Emails[0]).Should().BeEquivalentTo([Alice, Bob, Dave]);
        Readers(rule, Emails[1]).Should().BeEquivalentTo([Alice, Dave]);
        Readers(rule, Emails[2]).Should().BeEquivalentTo([Alice, Bob, Dave]);
        Readers(rule, Emails[3]).Should().BeEmpty("a mail without an owner has no readers through its owners");
        Readers(rule, Emails[4]).Should().BeEmpty("the invoice is not a contact");
    }

    [Fact]
    public void Inverse_IsRefused_WhenTheOwnerRuleIsOpaque() {
        var opaque = QueryAuthorizers.When(user => user.IsInRole("Admin"), QueryAuthorizers.Unrestricted<Contact>());
        var rule = QueryAuthorizers.AnyOf(
            QueryAuthorizers.Rows<Email>(email => email.Id == 1),
            QueryAuthorizers.Via<Email, Contact>(email => email.Contacts, opaque));

        var ask = () => Readers(rule, Emails[0]);

        ask.Should().Throw<NotSupportedException>().WithMessage("*'QueryAuthorizers.When'*");
    }

    [Fact]
    public void Via_RefusesTheDefaultOperation() {
        var build = () => QueryAuthorizers.Via<Email, Contact>(email => email.Contacts, ContactRule, default(ResourceOperation));

        build.Should().Throw<ArgumentException>();
    }

    private static Guid[] Readers(IQueryAuthorizer<Email> rule, Email email) {
        return Everyone.WhereAuthorizedFor(new[] { email }.AsQueryable(), rule, Directory).ToArray();
    }

    private sealed record Contact(Guid Id, Guid OwnerId, bool IsRestricted);

    private sealed record Link(string Kind, Guid RecordId);

    private sealed record Email(int Id, IReadOnlyList<Contact> Contacts, IReadOnlyList<Link> Links);

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
