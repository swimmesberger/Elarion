using System.Text;
using AwesomeAssertions;
using Elarion.Contacts;
using Xunit;

namespace Elarion.Tests.Contacts;

/// <summary>
/// "Read and write group cards" (spec 0005, P2; FR-068–FR-071, ADR-0114), from sabre/vobject's VCardConverterTest
/// (testConvertGroupCard V05, testConvertIndividualCard V07) and Cyrus's put_missing_name (C28).
/// </summary>
public sealed class ContactGroupTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Card(string version, params string[] lines) =>
        "BEGIN:VCARD\r\nVERSION:" + version + "\r\n" + string.Concat(lines.Select(static l => l + "\r\n")) + "END:VCARD\r\n";

    private static async Task<Contact> ReadAsync(string card) {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(card));
        var read = await new ContactReader().ReadAsync(stream, cancellationToken: Ct);
        read.Errors.Should().BeEmpty();
        return read.Contacts.Single();
    }

    private static async Task<string> WriteAsync(Contact contact, ContactFormatVersion version) {
        using var stream = new MemoryStream();
        await ContactWriter.WriteAsync(stream, [contact], new ContactWriteOptions { Version = version }, Ct);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static readonly Contact Team = new() {
        Uid = "team-red",
        FormattedName = "Team red",
        Kind = ContactKind.Group,
        Members = [ContactMember.FromUid("anna"), ContactMember.FromUid("bernd")],
    };

    [Theory]
    [InlineData("3.0", "X-ADDRESSBOOKSERVER-KIND:group", "X-ADDRESSBOOKSERVER-MEMBER")]
    [InlineData("3.0", "X-ADDRESSBOOKSERVER-KIND:GROUP", "X-ADDRESSBOOKSERVER-MEMBER")]
    [InlineData("4.0", "KIND:group", "MEMBER")]
    [InlineData("4.0", "KIND:GROUP", "MEMBER")]
    public async Task GivenAGroupCardInEitherForm_WhenRead_ThenTheContactIsAGroupWithItsMembersInOrder(string version, string kind, string member) {
        var contact = await ReadAsync(Card(version, "UID:team-red", "FN:Team red", kind, member + ":urn:uuid:anna", member + ":mailto:bernd@example.com"));

        contact.Kind.Should().Be(ContactKind.Group);
        contact.Members.Should().Equal("urn:uuid:anna", "mailto:bernd@example.com");
        contact.AdditionalProperties.Should().BeEmpty();
    }

    [Fact]
    public async Task GivenAGroup_WhenWrittenAs30_ThenItHoldsApplesFormAndAnNWithItsName() {
        var card = await WriteAsync(Team, ContactFormatVersion.V3_0);

        card.Should().Contain("X-ADDRESSBOOKSERVER-KIND:group\r\n").And.Contain("X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:anna\r\n")
            .And.Contain("X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:bernd\r\n").And.Contain("\r\nN:Team red;;;;\r\n").And.Contain("FN:Team red")
            .And.NotContain("\r\nKIND").And.NotContain("\r\nMEMBER");
    }

    [Fact]
    public async Task GivenAGroup_WhenWrittenAs40_ThenItHoldsKindGroupAndMembersButNoneOfApplesProperties() {
        var card = await WriteAsync(Team, ContactFormatVersion.V4_0);

        card.Should().Contain("KIND:group\r\n").And.Contain("MEMBER:urn:uuid:anna\r\n").And.Contain("MEMBER:urn:uuid:bernd\r\n")
            .And.NotContain("X-ADDRESSBOOKSERVER");
    }

    // sabre V05: 3.0 → 4.0 → 3.0 states the same group.
    [Fact]
    public async Task GivenA30GroupCard_WhenWrittenAs40AndBackAs30_ThenEachCardStatesTheSameGroup() {
        var read = await ReadAsync(Card("3.0", "UID:g", "FN:Group", "N:Group;;;;", "X-ADDRESSBOOKSERVER-KIND:group",
            "X-ADDRESSBOOKSERVER-MEMBER:mailto:a@example.com", "X-ADDRESSBOOKSERVER-MEMBER:mailto:b@example.com"));

        var as40 = await ReadAsync(await WriteAsync(read, ContactFormatVersion.V4_0));
        var back = await ReadAsync(await WriteAsync(as40, ContactFormatVersion.V3_0));

        as40.Should().Be(read);
        back.Should().Be(read);
        back.Members.Should().Equal("mailto:a@example.com", "mailto:b@example.com");
    }

    // sabre V07: KIND:individual is the default; 3.0 has no kind for it.
    [Fact]
    public async Task GivenA40IndividualCard_WhenWrittenAs30_ThenTheCardHoldsNoKind() {
        var contact = await ReadAsync(Card("4.0", "UID:i", "FN:Ina", "KIND:individual"));

        var card = await WriteAsync(contact, ContactFormatVersion.V3_0);

        contact.Kind.Should().Be(ContactKind.Individual);
        card.Should().NotContain("KIND");
    }

    // Cyrus C28: a 4.0 group with an escaped name and no N gets one in 3.0.
    [Fact]
    public async Task AGroupWithoutN_GetsItsNameAsNIn30() {
        var contact = await ReadAsync(Card("4.0", "UID:g", "FN:My\\;Group", "KIND:group"));

        var card = await WriteAsync(contact, ContactFormatVersion.V3_0);

        card.Should().Contain("\r\nN:My\\;Group;;;;\r\n");
    }

    [Fact]
    public async Task GivenAContactThatIsNotAGroupButHasMembers_WhenWritten_ThenTheWriteFailsNamingMember() {
        var contact = new Contact { Uid = "x", FormattedName = "Not a group", Members = ["urn:uuid:a"] };

        var write = () => WriteAsync(contact, ContactFormatVersion.V3_0);

        (await write.Should().ThrowAsync<ContactWriteException>()).Which.Property.Should().Be("MEMBER");
    }

    [Fact]
    public void FromUid_BuildsAUrnUuidReference() {
        ContactMember.FromUid("anna").Should().Be("urn:uuid:anna");
        FluentActions.Invoking(() => ContactMember.FromUid("")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GroupsWithDifferentMembers_AreNotEqual() {
        new Contact { Uid = "team-red", FormattedName = "Team red", Kind = ContactKind.Group, Members = ["urn:uuid:anna", "urn:uuid:bernd"] }
            .Should().Be(Team);
        Team.Should().NotBe(new Contact { Uid = "team-red", FormattedName = "Team red", Kind = ContactKind.Group, Members = [ContactMember.FromUid("anna")] });
    }
}
