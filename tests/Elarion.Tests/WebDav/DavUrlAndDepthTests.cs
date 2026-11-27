using AwesomeAssertions;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.WebDav;

/// <summary>
/// URLs devices already stored and the <c>Depth</c> forms clients send: explicit collection segments, the principal with
/// and without its trailing slash, <c>Depth: 1,noroot</c>, and refusing a user who sees no collection.
/// </summary>
public sealed class DavUrlAndDepthTests {
    private static Dictionary<string, string> Depth(string value) => new() { ["Depth"] = value };

    [Theory]
    [InlineData("/principals/u1/")]
    [InlineData("/principals/u1")]
    public async Task ThePrincipal_ResolvesWithAndWithoutItsTrailingSlash_AndAnswersItsCanonicalHref(string path) {
        await using var harness = DavTestHarness.Create();

        var response = await harness.SendAsync("PROPFIND", path, DavTestHarness.PropFind("D:displayname"), headers: Depth("0"));

        response.Status.Should().Be(207);
        response.Responses.Keys.Should().Equal("/dav/principals/u1/");
    }

    [Theory]
    [InlineData("1,noroot")]
    [InlineData("1, noroot")]
    [InlineData("1,NoRoot")]
    public async Task DepthOneNoRoot_ListsTheChildrenWithoutTheResourceItself(string depth) {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "src", "a");
        harness.Protocol.Show("u1", "src", "b");

        var response = await harness.SendAsync("PROPFIND", "/things/u1/", DavTestHarness.PropFind("D:displayname"), headers: Depth(depth));

        response.Status.Should().Be(207);
        response.Responses.Keys.Should().BeEquivalentTo("/dav/things/u1/src.a/", "/dav/things/u1/src.b/");
    }

    [Fact]
    public async Task DepthZeroNoRoot_AnswersAnEmptyMultistatus() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "src", "a");

        var response = await harness.SendAsync("PROPFIND", "/things/u1/src.a/", DavTestHarness.PropFind("D:displayname"), headers: Depth("0,noroot"));

        response.Status.Should().Be(207);
        response.Responses.Should().BeEmpty();
    }

    [Theory]
    [InlineData("1,bogus")]
    [InlineData("2")]
    [InlineData(",noroot")]
    public async Task AnUnknownDepth_Answers400(string depth) {
        await using var harness = DavTestHarness.Create();

        var response = await harness.SendAsync("PROPFIND", "/principals/u1/", DavTestHarness.PropFind("D:displayname"), headers: Depth(depth));

        response.Status.Should().Be(400);
    }

    [Fact]
    public async Task DepthInfinityNoRoot_IsStillRefusedAsInfinite() {
        await using var harness = DavTestHarness.Create();

        var response = await harness.SendAsync("PROPFIND", "/principals/u1/", DavTestHarness.PropFind("D:displayname"), headers: Depth("infinity,noroot"));

        response.Status.Should().Be(403);
        response.Body.Should().Contain("propfind-finite-depth");
    }

    [Fact]
    public async Task AnExplicitSegment_IsTheCollectionsUrl_AndTheStoreAddressKeepsItsToken() {
        await using var harness = DavTestHarness.Create();
        var collection = new DavCollection(TestThingsProtocol.Address("src", "a"), "Default") { Segment = "default" };
        harness.Protocol.Visible["u1"] = [collection];
        await harness.PublishAsync("src", "a", "one", "first");

        var listing = await harness.SendAsync("PROPFIND", "/things/u1/", DavTestHarness.PropFind("D:sync-token"), headers: Depth("1"));
        var member = await harness.SendAsync("GET", "/things/u1/default/one.txt");
        var old = await harness.SendAsync("PROPFIND", "/things/u1/src.a/", DavTestHarness.PropFind("D:displayname"), headers: Depth("0"));
        var otherCase = await harness.SendAsync("PROPFIND", "/things/u1/Default/", DavTestHarness.PropFind("D:displayname"), headers: Depth("0"));

        listing.Prop("/dav/things/u1/default/", DavNames.D.SyncToken)!.Text.Should()
            .Be(DavSyncToken.Format(TestThingsProtocol.Address("src", "a").Id, 1));
        member.Status.Should().Be(200);
        member.Body.Should().Be("first");
        old.Status.Should().Be(404);
        otherCase.Status.Should().Be(404, "a segment is matched exactly, so every collection has one URL");
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("é")]
    public void AnInvalidSegment_IsRefused(string segment) {
        var create = () => new DavCollection(TestThingsProtocol.Address("src", "a"), "A") { Segment = segment };

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ASegmentLongerThan128Characters_IsRefused_And128AreAccepted() {
        var address = TestThingsProtocol.Address("src", "a");

        new DavCollection(address, "A") { Segment = new string('a', 128) }.Segment.Should().HaveLength(128);
        ((Func<DavCollection>)(() => new DavCollection(address, "A") { Segment = new string('a', 129) })).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task RefuseWhenNoCollections_Answers403ToAUserWhoSeesNone_AndServesOneWhoSeesSome() {
        await using var harness = DavTestHarness.Create(options: o => o.RefuseWhenNoCollections = true);
        harness.Protocol.Show("u1", "src", "a");

        var seesNone = await harness.SendAsync("PROPFIND", "/principals/u2/", DavTestHarness.PropFind("D:displayname"), user: "u2", headers: Depth("0"));
        var seesNoneRoot = await harness.SendAsync("PROPFIND", "/", DavTestHarness.PropFind("D:current-user-principal"), user: "u2", headers: Depth("0"));
        var seesNoneWrite = await harness.SendAsync("PUT", "/things/u2/x/y.txt", "x", user: "u2");
        var options = await harness.SendAsync("OPTIONS", "/", user: "u2");
        var seesSome = await harness.SendAsync("PROPFIND", "/principals/u1/", DavTestHarness.PropFind("D:displayname"), headers: Depth("0"));

        seesNone.Status.Should().Be(403);
        seesNoneRoot.Status.Should().Be(403);
        seesNoneWrite.Status.Should().Be(403);
        options.Status.Should().Be(200);
        seesSome.Status.Should().Be(207);
    }

    [Fact]
    public async Task ByDefault_AUserWhoSeesNoCollection_DiscoversAnEmptyHome() {
        await using var harness = DavTestHarness.Create();

        var home = await harness.SendAsync("PROPFIND", "/things/u2/", DavTestHarness.PropFind("D:displayname"), user: "u2", headers: Depth("1"));

        home.Status.Should().Be(207);
        home.Responses.Keys.Should().Equal("/dav/things/u2/");
    }
}
