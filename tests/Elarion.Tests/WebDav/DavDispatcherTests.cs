using System.Text;
using AwesomeAssertions;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.WebDav;

/// <summary>The WebDAV dispatcher in process over the test-only collection kind (spec 0006 FR-014–FR-025).</summary>
public sealed class DavDispatcherTests {
    private static readonly DavName Shape = new(TestThingsProtocol.Ns, "shape");
    private static readonly DavName HomeSet = new(TestThingsProtocol.Ns, "thing-home-set");

    [Fact]
    public async Task Options_AnswersTheDavHeaderAndAnAllowListWithoutWriteMethods() {
        await using var harness = DavTestHarness.Create();

        var response = await harness.SendAsync("OPTIONS", "/principals/u1/");

        response.Status.Should().Be(200);
        response.Headers["DAV"].Should().Be("1, 3, thing-access");
        response.Headers["Allow"].Should().Be("OPTIONS, GET, HEAD, PROPFIND, REPORT");
        foreach (var write in new[] { "PUT", "DELETE", "PROPPATCH", "MKCALENDAR", "MOVE", "COPY" })
            response.Headers["Allow"].Should().NotContain(write);
    }

    [Fact]
    public async Task AnUnauthenticatedRequest_Answers401() {
        await using var harness = DavTestHarness.Create();

        (await harness.SendAsync("PROPFIND", "/", user: "")).Status.Should().Be(401);
    }

    [Fact]
    public async Task PropFindOnTheRoot_AnswersTheCurrentUserPrincipal() {
        await using var harness = DavTestHarness.Create();

        var response = await harness.SendAsync("PROPFIND", "/", DavTestHarness.PropFind("D:current-user-principal", "D:resourcetype"),
            headers: Depth("0"));

        response.Status.Should().Be(207);
        response.Prop("/dav/", DavNames.D.CurrentUserPrincipal)!.Child(DavNames.D.Href)!.Text.Should().Be("/dav/principals/u1/");
        response.Prop("/dav/", DavNames.D.ResourceType)!.Child(DavNames.D.Collection).Should().NotBeNull();
    }

    [Fact]
    public async Task PropFindOnThePrincipal_AnswersItsPropertiesAndAnUnknownOneAs404() {
        await using var harness = DavTestHarness.Create();

        var response = await harness.SendAsync("PROPFIND", "/principals/u1/",
            DavTestHarness.PropFind("D:principal-URL", "D:displayname", "D:resourcetype", "T:thing-home-set", "D:quota-used-bytes"),
            headers: Depth("0"));

        response.Status.Should().Be(207);
        const string href = "/dav/principals/u1/";
        response.Prop(href, DavNames.D.PrincipalUrl)!.Child(DavNames.D.Href)!.Text.Should().Be(href);
        response.Prop(href, DavNames.D.DisplayName)!.Text.Should().Be("u1@example.com");
        response.Prop(href, DavNames.D.ResourceType)!.Child(DavNames.D.Principal).Should().NotBeNull();
        response.Prop(href, HomeSet)!.Child(DavNames.D.Href)!.Text.Should().Be("/dav/things/u1/");
        response.IsMissing(href, new DavName(DavNames.Dav, "quota-used-bytes")).Should().BeTrue();
    }

    [Fact]
    public async Task ReturnMinimal_LeavesUnknownPropertiesOut() {
        await using var harness = DavTestHarness.Create();

        var response = await harness.SendAsync("PROPFIND", "/principals/u1/", DavTestHarness.PropFind("D:displayname", "D:quota-used-bytes"),
            headers: new Dictionary<string, string> { ["Depth"] = "0", ["Prefer"] = "return=minimal" });

        response.IsMissing("/dav/principals/u1/", new DavName(DavNames.Dav, "quota-used-bytes")).Should().BeFalse();
        response.Prop("/dav/principals/u1/", DavNames.D.DisplayName).Should().NotBeNull();
    }

    [Fact]
    public async Task PropFindWithDepthInfinity_Answers403PropFindFiniteDepth() {
        await using var harness = DavTestHarness.Create();

        var response = await harness.SendAsync("PROPFIND", "/things/u1/", DavTestHarness.PropFind("D:displayname"), headers: Depth("infinity"));

        response.Status.Should().Be(403);
        response.Xml.Child(DavNames.D.PropFindFiniteDepth).Should().NotBeNull();
    }

    [Fact]
    public async Task APrincipalOrHomeOfAnotherUser_Answers404() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u2", "shifts", "k");

        (await harness.SendAsync("PROPFIND", "/principals/u2/", headers: Depth("0"))).Status.Should().Be(404);
        (await harness.SendAsync("PROPFIND", "/things/u2/", headers: Depth("1"))).Status.Should().Be(404);
        (await harness.SendAsync("PROPFIND", "/things/u2/shifts.k/", headers: Depth("1"))).Status.Should().Be(404);
        (await harness.SendAsync("PROPFIND", "/things/u1/shifts.k/", headers: Depth("1"))).Status.Should().Be(404);
    }

    [Fact]
    public async Task TheHomeWithDepth1_ListsTheVisibleCollectionsReadOnlyWithTokens() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine", "My shifts");
        await harness.PublishAsync("shifts", "mine", "a", "A");

        var response = await harness.SendAsync("PROPFIND", "/things/u1/",
            DavTestHarness.PropFind("D:resourcetype", "D:displayname", "D:sync-token", "CS:getctag", "D:current-user-privilege-set",
                "D:supported-report-set", "D:owner", "T:shape"),
            headers: Depth("1"));

        response.Status.Should().Be(207);
        response.Responses.Keys.Should().BeEquivalentTo("/dav/things/u1/", "/dav/things/u1/shifts.mine/");
        const string calendar = "/dav/things/u1/shifts.mine/";
        response.Prop(calendar, DavNames.D.DisplayName)!.Text.Should().Be("My shifts");
        response.Prop(calendar, DavNames.D.ResourceType)!.Child(new DavName(TestThingsProtocol.Ns, "things")).Should().NotBeNull();
        var token = response.Prop(calendar, DavNames.D.SyncToken)!.Text;
        token.Should().StartWith("urn:elarion:sync:").And.EndWith(":1");
        response.Prop(calendar, new DavName(DavNames.CalendarServer, "getctag"))!.Text.Should().Be(token);
        response.Prop(calendar, Shape)!.Text.Should().Be("round:shifts.mine");
        response.Prop(calendar, DavNames.D.Owner)!.Child(DavNames.D.Href)!.Text.Should().Be("/dav/principals/u1/");
        var privileges = response.Prop(calendar, DavNames.D.CurrentUserPrivilegeSet)!.ChildrenNamed(DavNames.D.Privilege)
            .SelectMany(static p => p.Children).Select(static p => p.Name.LocalName);
        privileges.Should().BeEquivalentTo("read", "read-current-user-privilege-set");
        response.Prop(calendar, DavNames.D.SupportedReportSet)!.Body().Should().Contain("sync-collection");
    }

    [Fact]
    public async Task ACollectionNothingWasPublishedTo_HasAStableTokenAtZero() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "empty");

        var response = await harness.SendAsync("PROPFIND", "/things/u1/shifts.empty/", DavTestHarness.PropFind("D:sync-token"), headers: Depth("0"));

        response.Prop("/dav/things/u1/shifts.empty/", DavNames.D.SyncToken)!.Text
            .Should().Be(DavSyncToken.Format(TestThingsProtocol.Address("shifts", "empty").Id, 0));
    }

    [Fact]
    public async Task TheCollectionWithDepth1_ListsMembersWithStrongETagsAndContentTypes() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");
        await harness.PublishAsync("shifts", "mine", "a", "A");
        await harness.PublishAsync("shifts", "mine", "b", "B");

        var response = await harness.SendAsync("PROPFIND", "/things/u1/shifts.mine/", DavTestHarness.PropFind("D:getetag", "D:getcontenttype"),
            headers: Depth("1"));

        response.Responses.Keys.Should().BeEquivalentTo("/dav/things/u1/shifts.mine/", "/dav/things/u1/shifts.mine/a.txt",
            "/dav/things/u1/shifts.mine/b.txt");
        response.Prop("/dav/things/u1/shifts.mine/a.txt", DavNames.D.GetETag)!.Text.Should().Be(DavETag.Compute("A"u8));
        response.Prop("/dav/things/u1/shifts.mine/a.txt", DavNames.D.GetContentType)!.Text.Should().Be("text/plain; charset=utf-8");
        response.IsMissing("/dav/things/u1/shifts.mine/", DavNames.D.GetETag).Should().BeTrue();
    }

    [Fact]
    public async Task AllPropAndPropName_ShareTheTable() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");

        var all = await harness.SendAsync("PROPFIND", "/things/u1/shifts.mine/", headers: Depth("0"));
        var names = await harness.SendAsync("PROPFIND", "/things/u1/shifts.mine/",
            "<D:propfind xmlns:D=\"DAV:\"><D:propname/></D:propfind>", headers: Depth("0"));

        all.Prop("/dav/things/u1/shifts.mine/", Shape).Should().NotBeNull();
        all.Prop("/dav/things/u1/shifts.mine/", DavNames.D.CurrentUserPrivilegeSet).Should().BeNull("allprop leaves out the expensive ACL properties");
        var listed = names.Prop("/dav/things/u1/shifts.mine/", DavNames.D.SyncToken);
        listed.Should().NotBeNull();
        listed!.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOnAMember_AnswersItsBytesAndETag_AndHonoursIfNoneMatch() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");
        await harness.PublishAsync("shifts", "mine", "a", "Alpha");

        var get = await harness.SendAsync("GET", "/things/u1/shifts.mine/a.txt");
        var notModified = await harness.SendAsync("GET", "/things/u1/shifts.mine/a.txt",
            headers: new Dictionary<string, string> { ["If-None-Match"] = DavETag.Compute("Alpha"u8) });

        get.Status.Should().Be(200);
        get.Body.Should().Be("Alpha");
        get.Headers["ETag"].Should().Be(DavETag.Compute("Alpha"u8));
        get.ContentType.Should().Be("text/plain; charset=utf-8");
        notModified.Status.Should().Be(304);
        (await harness.SendAsync("GET", "/things/u1/shifts.mine/missing.txt")).Status.Should().Be(404);
    }

    [Theory]
    [InlineData("PUT", "/things/u1/shifts.mine/a.txt", "write-content")]
    [InlineData("PUT", "/things/u1/shifts.mine/new.txt", "write-content")]
    [InlineData("DELETE", "/things/u1/shifts.mine/a.txt", "unbind")]
    [InlineData("PROPPATCH", "/things/u1/shifts.mine/", "write-properties")]
    [InlineData("MKCALENDAR", "/things/u1/other/", "bind")]
    [InlineData("MOVE", "/things/u1/shifts.mine/a.txt", "unbind")]
    [InlineData("COPY", "/things/u1/shifts.mine/a.txt", "bind")]
    public async Task AWriteToAReadOnlyCollection_Answers403NeedPrivilegesAndChangesNothing(string method, string path, string privilege) {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");
        await harness.PublishAsync("shifts", "mine", "a", "Alpha");
        var before = await harness.SendAsync("PROPFIND", "/things/u1/shifts.mine/", DavTestHarness.PropFind("D:sync-token"), headers: Depth("0"));

        var response = await harness.SendAsync(method, path, "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");

        response.Status.Should().Be(403);
        var need = response.Xml.Child(DavNames.D.NeedPrivileges)!.Child(DavNames.D.Resource)!;
        need.Child(DavNames.D.Privilege)!.Children.Single().Name.LocalName.Should().Be(privilege);
        var after = await harness.SendAsync("PROPFIND", "/things/u1/shifts.mine/", DavTestHarness.PropFind("D:sync-token"), headers: Depth("0"));
        after.Body.Should().Be(before.Body);
        (await harness.SendAsync("GET", "/things/u1/shifts.mine/a.txt")).Body.Should().Be("Alpha");
    }

    [Fact]
    public async Task AWriteToACollectionTheUserDoesNotSee_Answers404() {
        await using var harness = DavTestHarness.Create();

        (await harness.SendAsync("PUT", "/things/u1/secret.k/a.txt", "x")).Status.Should().Be(404);
        (await harness.SendAsync("DELETE", "/things/u2/", "x")).Status.Should().Be(404);
    }

    [Fact]
    public async Task SyncCollection_InitialThenDelta_ReportsChangesAndRemovals() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");
        await harness.PublishAsync("shifts", "mine", "a", "A");
        await harness.PublishAsync("shifts", "mine", "b", "B");

        var initial = await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", DavTestHarness.SyncCollection(null, props: "T:data"));
        initial.Status.Should().Be(207);
        initial.Responses.Keys.Should().BeEquivalentTo("/dav/things/u1/shifts.mine/a.txt", "/dav/things/u1/shifts.mine/b.txt");
        initial.Prop("/dav/things/u1/shifts.mine/a.txt", new DavName(TestThingsProtocol.Ns, "data"))!.Text.Should().Be("A");
        var token = initial.SyncToken!;

        await harness.PublishAsync("shifts", "mine", "a", "A2");
        await harness.RemoveAsync("shifts", "mine", "b");
        await harness.PublishAsync("shifts", "mine", "c", "C");

        var delta = await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", DavTestHarness.SyncCollection(token));
        delta.Status.Should().Be(207);
        delta.Responses.Keys.Should().Equal("/dav/things/u1/shifts.mine/a.txt", "/dav/things/u1/shifts.mine/b.txt",
            "/dav/things/u1/shifts.mine/c.txt");
        delta.Responses["/dav/things/u1/shifts.mine/b.txt"].Child(DavNames.D.Status)!.Text.Should().Contain("404");
        delta.Prop("/dav/things/u1/shifts.mine/a.txt", DavNames.D.GetETag)!.Text.Should().Be(DavETag.Compute("A2"u8));
        delta.SyncToken.Should().NotBe(token);

        var unchanged = await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", DavTestHarness.SyncCollection(delta.SyncToken));
        unchanged.Responses.Should().BeEmpty();
        unchanged.SyncToken.Should().Be(delta.SyncToken);
    }

    [Fact]
    public async Task SyncCollection_WithALimit_Answers507AndContinuesFromItsToken() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");
        var initial = await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", DavTestHarness.SyncCollection(null));
        foreach (var uid in new[] { "a", "b", "c", "d", "e" }) await harness.PublishAsync("shifts", "mine", uid, uid);

        var first = await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", DavTestHarness.SyncCollection(initial.SyncToken, limit: 2));
        first.Responses.Keys.Should().Equal("/dav/things/u1/shifts.mine/a.txt", "/dav/things/u1/shifts.mine/b.txt", "/dav/things/u1/shifts.mine/");
        first.Responses["/dav/things/u1/shifts.mine/"].Child(DavNames.D.Status)!.Text.Should().Contain("507");
        var rest = await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", DavTestHarness.SyncCollection(first.SyncToken));

        rest.Responses.Keys.Should().Equal("/dav/things/u1/shifts.mine/c.txt", "/dav/things/u1/shifts.mine/d.txt", "/dav/things/u1/shifts.mine/e.txt");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("urn:elarion:sync:6f9619ff-8b86-d011-b42d-00cf4fc964ff:1")]
    [InlineData("http://sabre.io/ns/sync/3")]
    public async Task SyncCollection_WithAForeignOrUnknownToken_Answers403ValidSyncToken(string token) {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");

        var response = await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", DavTestHarness.SyncCollection(token));

        response.Status.Should().Be(403);
        response.Xml.Child(DavNames.D.ValidSyncToken).Should().NotBeNull();
    }

    [Fact]
    public async Task SyncCollection_WithATokenFromTheFuture_Answers403ValidSyncToken() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");
        var future = DavSyncToken.Format(TestThingsProtocol.Address("shifts", "mine").Id, 50);

        (await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", DavTestHarness.SyncCollection(future))).Status.Should().Be(403);
    }

    [Fact]
    public async Task ATokenOlderThanTheJournalsRetention_Answers403AndAFullResyncEndsWithTheSameObjects() {
        await using var harness = DavTestHarness.Create(options: o => o.MaxJournalRowsPerCollection = 2);
        harness.Protocol.Show("u1", "shifts", "mine");
        await harness.PublishAsync("shifts", "mine", "a", "A");
        var old = (await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", DavTestHarness.SyncCollection(null))).SyncToken;
        await harness.PublishAsync("shifts", "mine", "b", "B");
        await harness.PublishAsync("shifts", "mine", "c", "C");
        await harness.PublishAsync("shifts", "mine", "d", "D");

        var expired = await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", DavTestHarness.SyncCollection(old));
        var full = await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", DavTestHarness.SyncCollection(null));

        expired.Status.Should().Be(403);
        expired.Xml.Child(DavNames.D.ValidSyncToken).Should().NotBeNull();
        full.Responses.Keys.Should().BeEquivalentTo("/dav/things/u1/shifts.mine/a.txt", "/dav/things/u1/shifts.mine/b.txt",
            "/dav/things/u1/shifts.mine/c.txt", "/dav/things/u1/shifts.mine/d.txt");
    }

    [Fact]
    public async Task SyncCollection_WithLevelInfinity_Answers403SyncTraversalSupported() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");

        var response = await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", DavTestHarness.SyncCollection(null, level: "infinity"));

        response.Status.Should().Be(403);
        response.Xml.Child(DavNames.D.SyncTraversalSupported).Should().NotBeNull();
    }

    [Fact]
    public async Task AnUnknownReport_Answers403SupportedReport() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");

        var response = await harness.SendAsync("REPORT", "/things/u1/shifts.mine/", "<D:expand-property xmlns:D=\"DAV:\"/>");

        response.Status.Should().Be(403);
        response.Xml.Child(DavNames.D.SupportedReport).Should().NotBeNull();
    }

    [Fact]
    public async Task TheCollectionsAreResolvedOncePerRequest() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");

        await harness.SendAsync("PROPFIND", "/things/u1/shifts.mine/", headers: Depth("1"));

        harness.Protocol.ResolveCount.Should().Be(1);
    }

    private static Dictionary<string, string> Depth(string depth) => new() { ["Depth"] = depth };
}

internal static class DavXmlElementTestExtensions {
    /// <summary>The element's subtree as names, for loose assertions.</summary>
    public static string Body(this DavXmlElement element) {
        var builder = new StringBuilder();
        Append(element);
        return builder.ToString();

        void Append(DavXmlElement e) {
            builder.Append('<').Append(e.Name.LocalName).Append('>').Append(e.Text);
            foreach (var child in e.Children) Append(child);
        }
    }
}
