using System.Text;
using AwesomeAssertions;
using Elarion.CardDav;
using Elarion.Contacts;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>"Publish a contact a person sees on their phone" (spec 0007, P1) and FR-005–FR-010, FR-031.</summary>
public sealed class CardDavPublishingTests {
    private const string Book = "/addressbooks/alice/customers.all/";
    private const string BookHref = "/dav/addressbooks/alice/customers.all/";
    private static readonly CardDavAddress Customers = new("customers", "all");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> TokenAsync(CardDavTestHarness harness) =>
        (await harness.SendAsync("REPORT", Book, CardDavTestHarness.SyncCollection(null))).SyncToken!;

    [Fact]
    public async Task GivenAPublishedContact_WhenItIsPublishedAgainUnchanged_ThenTheSyncTokenDoesNotChange() {
        await using var harness = CardDavTestHarness.Create();
        (await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger", phone: "+43 1 234"))).Should().BeTrue();
        var token = await TokenAsync(harness);

        (await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger", phone: "+43 1 234"))).Should().BeFalse();

        (await TokenAsync(harness)).Should().Be(token);
    }

    [Fact]
    public async Task GivenAPublishedContact_WhenItsPhoneChanges_ThenTokenAndETagChangeAndASyncFromTheOldTokenReceivesIt() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger", phone: "+43 1 234"));
        var initial = await harness.SendAsync("REPORT", Book, CardDavTestHarness.SyncCollection(null));
        var etag = initial.Prop(BookHref + "c-1.vcf", DavNames.D.GetETag)!.Text;

        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger", phone: "+43 1 999"));
        var delta = await harness.SendAsync("REPORT", Book, CardDavTestHarness.SyncCollection(initial.SyncToken, "<CARD:address-data/>"));

        delta.SyncToken.Should().NotBe(initial.SyncToken);
        delta.Responses.Keys.Should().Equal(BookHref + "c-1.vcf");
        delta.Prop(BookHref + "c-1.vcf", DavNames.D.GetETag)!.Text.Should().NotBe(etag);
        delta.Prop(BookHref + "c-1.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("+43 1 999");
    }

    [Fact]
    public async Task GivenAPublishedContact_WhenItIsRemoved_ThenASyncFromTheOldTokenReceivesItAsRemoved() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger"));
        var token = await TokenAsync(harness);

        (await harness.PublisherAsync(p => p.RemoveAsync(Customers, "c-1", Ct).AsTask())).Should().BeTrue();
        var delta = await harness.SendAsync("REPORT", Book, CardDavTestHarness.SyncCollection(token));

        delta.Responses[BookHref + "c-1.vcf"].Child(DavNames.D.Status)!.Text.Should().Contain("404");
    }

    [Fact]
    public async Task GivenA300ContactAddressBook_WhenReplacedWith299OneChanged_ThenASyncReceivesExactlyOneChangeAndOneRemoval() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublisherAsync(p => p.ReplaceAsync(Customers,
            Enumerable.Range(0, 300).Select(static i => CardDavTestData.Person("c" + i, "Given" + i, "Family")), Ct).AsTask());
        var token = await TokenAsync(harness);

        var outcome = await harness.PublisherAsync(p => p.ReplaceAsync(Customers,
            Enumerable.Range(0, 299).Select(static i => CardDavTestData.Person("c" + i, "Given" + i, i == 42 ? "Changed" : "Family")), Ct).AsTask());
        var delta = await harness.SendAsync("REPORT", Book, CardDavTestHarness.SyncCollection(token));

        outcome.Should().Be(new DavReplaceOutcome(0, 1, 1));
        delta.Responses.Keys.Should().BeEquivalentTo(BookHref + "c42.vcf", BookHref + "c299.vcf");
        delta.Responses[BookHref + "c299.vcf"].Child(DavNames.D.Status)!.Text.Should().Contain("404");
    }

    [Fact]
    public async Task GivenAContactWithPhotoBirthdayWithoutYearAndOrganization_WhenPublished_ThenAClientReceivesOneVCard30CardWithThemUnderItsUid() {
        await using var harness = CardDavTestHarness.Create();
        var contact = new Contact {
            Uid = "c-photo",
            FormattedName = "Anna Berger",
            Name = new ContactName("Berger", "Anna", null, null, null),
            Organization = "Example GmbH",
            Birthday = new ContactDate(null, 4, 15),
            Photo = ContactPhoto.FromData(CardDavTestData.Png),
        };
        await harness.PublishAsync("customers", "all", contact);

        var get = await harness.SendAsync("GET", Book + "c-photo.vcf");

        get.Status.Should().Be(200);
        get.ContentType.Should().Be("text/vcard; charset=utf-8");
        var card = get.Body;
        card.Split("BEGIN:VCARD").Length.Should().Be(2);
        card.Should().Contain("VERSION:3.0").And.Contain("UID:c-photo").And.Contain("FN:Anna Berger").And.Contain("N:Berger;Anna;;;")
            .And.Contain("ORG:Example GmbH").And.Contain("BDAY;X-APPLE-OMIT-YEAR=1604:1604-04-15").And.Contain("PHOTO;ENCODING=b;TYPE=PNG:");
    }

    [Fact]
    public async Task APublishedCardAlwaysCarriesFnNUidAndVersion() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", new Contact { Uid = "c-org", Organization = "Example GmbH", Kind = ContactKind.Organization });

        var card = (await harness.SendAsync("GET", Book + "c-org.vcf")).Body;

        card.Should().Contain("VERSION:3.0").And.Contain("FN:Example GmbH").And.Contain("\r\nN:").And.Contain("UID:c-org");
    }

    [Fact]
    public async Task PublishingAContactWithoutUidOrName_Throws() {
        await using var harness = CardDavTestHarness.Create();

        await ((Func<Task>)(() => harness.PublishAsync("customers", "all", new Contact { FormattedName = "No UID" })))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*UID*");
        await ((Func<Task>)(() => harness.PublishAsync("customers", "all", new Contact { Uid = "nameless" })))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*nameless*");
    }

    [Fact]
    public async Task ReplacingWithTwoContactsOfOneUid_ThrowsAndChangesNothing() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger"));
        var token = await TokenAsync(harness);

        await ((Func<Task>)(() => harness.PublisherAsync(p => p.ReplaceAsync(Customers,
                [CardDavTestData.Person("dup", "A", "B"), CardDavTestData.Person("dup", "C", "D")], Ct).AsTask())))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*dup*");

        (await TokenAsync(harness)).Should().Be(token);
    }

    [Fact]
    public async Task PublishingIntoAnUnregisteredSource_Throws() {
        await using var harness = CardDavTestHarness.Create();

        await ((Func<Task>)(() => harness.PublishAsync("nope", "all", CardDavTestData.Person("c-1", "Anna", "Berger"))))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*AddElarionCardDavSource*");
    }

    [Fact]
    public async Task AUidThatIsNoSafeFileName_GetsAStableHashedName() {
        await using var harness = CardDavTestHarness.Create();
        const string uid = "urn:uuid:Ä/1 2";
        await harness.PublishAsync("customers", "all", CardDavTestData.Person(uid, "Anna", "Berger"));

        var listing = await harness.SendAsync("PROPFIND", Book, CardDavTestHarness.PropFind("D:getetag"), headers: new Dictionary<string, string> { ["Depth"] = "1" });
        var member = listing.Responses.Keys.Single(static href => href != BookHref);

        member.Should().EndWith(".vcf").And.StartWith(BookHref);
        CardDavObjects.NameOf(uid).Should().Be(CardDavObjects.NameOf(uid)).And.NotContain("/");
        Encoding.ASCII.GetByteCount(member).Should().Be(member.Length);
    }

    [Fact]
    public async Task ACalendarSourceAndAnAddressBookSourceOfOneName_ShareNoState() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger"));

        new CardDavAddress("customers", "all").ToCollectionAddress().Id
            .Should().NotBe(new DavCollectionAddress("calendar", "customers", "all").Id);
    }
}
