using AwesomeAssertions;
using Elarion.CardDav;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>An address book's explicit URL segment (spec 0007 FR-042): URLs devices already stored keep working.</summary>
public sealed class CardDavSegmentTests {
    private static Dictionary<string, string> Depth(string value) => new() { ["Depth"] = value };

    [Fact]
    public async Task APublishedAddressBookWithASegment_IsServedThere_AndKeepsItsSyncToken() {
        await using var harness = CardDavTestHarness.Create();
        harness.Directory.Override = [new PublishedAddressBookInfo("all", "Customers") { Segment = "default" }];
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-1", "Ada", "Lovelace"));

        var home = await harness.SendAsync("PROPFIND", "/addressbooks/alice/", CardDavTestHarness.PropFind("D:sync-token"), headers: Depth("1"));
        var get = await harness.SendAsync("GET", "/addressbooks/alice/default/c-1.vcf");
        var old = await harness.SendAsync("GET", "/addressbooks/alice/customers.all/c-1.vcf");

        home.Prop("/dav/addressbooks/alice/default/", DavNames.D.SyncToken)!.Text.Should()
            .Be(DavSyncToken.Format(new CardDavAddress("customers", "all").ToCollectionAddress().Id, 1));
        get.Status.Should().Be(200);
        old.Status.Should().Be(404);
    }

    [Fact]
    public async Task TwoAddressBooksWithTheSameSegment_FailAsTheApplicationsMistake() {
        await using var harness = CardDavTestHarness.Create();
        harness.Directory.Override = [
            new PublishedAddressBookInfo("all", "Customers") { Segment = "default" },
            new PublishedAddressBookInfo("vip", "VIP") { Segment = "default" },
        ];

        var request = () => harness.SendAsync("PROPFIND", "/addressbooks/alice/", CardDavTestHarness.PropFind("D:displayname"), headers: Depth("1"));

        (await request.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("'default'");
    }

    [Fact]
    public async Task AnExplicitSegmentEqualToAnotherSourcesDefault_FailsToo() {
        await using var harness = CardDavTestHarness.Create();
        harness.Directory.Override = [new PublishedAddressBookInfo("all", "Customers") { Segment = "teams.red" }];

        var request = () => harness.SendAsync("PROPFIND", "/addressbooks/alice/", CardDavTestHarness.PropFind("D:displayname"), headers: Depth("1"));

        await request.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task AnInvalidSegment_FailsNamingTheSource(string segment) {
        await using var harness = CardDavTestHarness.Create();
        harness.Directory.Override = [new PublishedAddressBookInfo("all", "Customers") { Segment = segment }];

        var request = () => harness.SendAsync("PROPFIND", "/addressbooks/alice/", CardDavTestHarness.PropFind("D:displayname"), headers: Depth("1"));

        (await request.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("customers");
    }

    [Fact]
    public void MemberNameOf_IsThePublishedContactsName() {
        CardDavAddress.MemberNameOf("c-1").Should().Be("c-1.vcf");
        CardDavAddress.MemberNameOf("a b").Should().NotContain(" ").And.EndWith(".vcf");
    }
}
