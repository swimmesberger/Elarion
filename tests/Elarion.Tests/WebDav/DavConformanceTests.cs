using AwesomeAssertions;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.WebDav;

/// <summary>
/// Protocol-generic WebDAV and RFC 6578 cases ported from other servers' and clients' suites (Apple ccs-caldavtester,
/// litmus, sabre/dav, Radicale, Xandikos, Cyrus, python-caldav, DAVx5, vdirsyncer), over the test-only collection kind.
/// Each test names the cases it ports; where a suite disagrees with a documented Elarion choice, the test pins the choice.
/// </summary>
public sealed class DavConformanceTests {
    private const string Collection = "/things/u1/shifts.mine/";
    private const string CollectionHref = "/dav/things/u1/shifts.mine/";
    private static readonly DavName Data = new(TestThingsProtocol.Ns, "data");

    private static async Task<DavTestHarness> WithMembersAsync(params string[] uids) {
        var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");
        foreach (var uid in uids) await harness.PublishAsync("shifts", "mine", uid, uid.ToUpperInvariant());
        return harness;
    }

    private static Dictionary<string, string> Depth(string depth) => new() { ["Depth"] = depth };

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) =>
        headers.ToDictionary(static h => h.Name, static h => h.Value);

    private static IEnumerable<DavXmlElement> PropStats(DavTestResponse response, string status) =>
        response.Xml.ChildrenNamed(DavNames.D.Response).SelectMany(static r => r.ChildrenNamed(DavNames.D.Propstat))
            .Where(p => (p.Child(DavNames.D.Status)?.Text ?? "").Contains(" " + status + " ", StringComparison.Ordinal));

    // ---- OPTIONS and the DAV header ----

    /// <summary>sabre/dav ServerSimpleTest testOptionsUnmapped (W36): OPTIONS on a path that does not exist still succeeds.</summary>
    [Fact]
    public async Task Options_OnAPathThatDoesNotExist_Answers200WithTheDavHeaderAndNoWriteMethod() {
        await using var harness = DavTestHarness.Create();

        var response = await harness.SendAsync("OPTIONS", "/things/u1/nothing.here/x.txt");

        response.Status.Should().Be(200);
        response.Headers["DAV"].Should().StartWith("1, 3");
        response.Headers["Allow"].Should().NotContain("MKCOL").And.NotContain("PUT");
    }

    /// <summary>
    /// Apple options.xml "PROPFIND - no DAV" (D-08) expects no DAV header on a PROPFIND answer. Elarion sends it there on
    /// purpose (macOS reads it from PROPFIND answers, DavDispatcher.PropFindAsync); this pins that choice.
    /// </summary>
    [Fact]
    public async Task PropFind_CarriesTheDavHeader_AsElarionChoseForMacOs() {
        await using var harness = DavTestHarness.Create();

        var response = await harness.SendAsync("PROPFIND", "/principals/u1/", DavTestHarness.PropFind("D:displayname"), headers: Depth("0"));

        response.Status.Should().Be(207);
        response.Headers["DAV"].Should().Be("1, 3, thing-access");
    }

    // ---- PROPFIND: Depth ----

    /// <summary>
    /// Apple propfind.xml "Depth:infinity disabled" 1–7 (P-14) reads a missing Depth as infinity (RFC 4918 §9.1);
    /// sabre/dav ServerPropsTest testPropFindEmptyBodyFile (W03) as the method's default. Elarion reads it as 1 (dav-context):
    /// pinned for a collection (members listed, no 403) and a member (only itself).
    /// </summary>
    [Fact]
    public async Task PropFindWithoutDepth_IsReadAsDepth1_OnACollectionAndOnAMember() {
        await using var harness = await WithMembersAsync("a", "b");

        var collection = await harness.SendAsync("PROPFIND", Collection, DavTestHarness.PropFind("D:getetag"));
        var member = await harness.SendAsync("PROPFIND", Collection + "a.txt", DavTestHarness.PropFind("D:getcontentlength"));

        collection.Status.Should().Be(207);
        collection.Responses.Keys.Should().BeEquivalentTo(CollectionHref, CollectionHref + "a.txt", CollectionHref + "b.txt");
        member.Responses.Keys.Should().Equal(CollectionHref + "a.txt");
        member.Prop(CollectionHref + "a.txt", DavNames.D.GetContentLength)!.Text.Should().Be("1");
    }

    /// <summary>
    /// A member's data property named in a PROPFIND carries the content (RFC 6352 §10.4, RFC 4791 §9.6; sabre/dav answers
    /// it): at depth 1 for every member, at depth 0 for one. It used to answer empty in a 200 propstat, because a listed
    /// member's missing content read as an empty one.
    /// </summary>
    [Fact]
    public async Task PropFindNamingADataProperty_CarriesEachMembersContent() {
        await using var harness = await WithMembersAsync("a", "b");

        var listing = await harness.SendAsync("PROPFIND", Collection, DavTestHarness.PropFind("D:getetag", "T:data"), headers: Depth("1"));
        var member = await harness.SendAsync("PROPFIND", Collection + "b.txt", DavTestHarness.PropFind("T:data"), headers: Depth("0"));

        listing.Prop(CollectionHref + "a.txt", Data)!.Text.Should().Be("A");
        listing.Prop(CollectionHref + "b.txt", Data)!.Text.Should().Be("B");
        listing.IsMissing(CollectionHref, Data).Should().BeTrue();
        member.Prop(CollectionHref + "b.txt", Data)!.Text.Should().Be("B");
    }

    /// <summary>
    /// sabre/dav Server::getHTTPDepth (W10) falls back to the default for a garbage Depth; Elarion refuses it with 400
    /// (DavDispatcher.PropFindAsync). Pinned.
    /// </summary>
    [Theory]
    [InlineData("2")]
    [InlineData("foo")]
    public async Task PropFindWithAnInvalidDepth_Answers400_AsElarionChose(string depth) {
        await using var harness = await WithMembersAsync("a");

        (await harness.SendAsync("PROPFIND", Collection, DavTestHarness.PropFind("D:getetag"), headers: Depth(depth))).Status.Should().Be(400);
    }

    /// <summary>
    /// Apple propfind.xml "prop" 3 on calendar and address book (P-02, P-03): Depth infinity is refused for every body
    /// shape, not only for a prop list.
    /// </summary>
    [Theory]
    [InlineData("<D:propfind xmlns:D=\"DAV:\"><D:propname/></D:propfind>")]
    [InlineData("<D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>")]
    [InlineData(null)]
    public async Task PropFindWithDepthInfinity_Answers403ForPropNameAllPropAndAnEmptyBody(string? body) {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("PROPFIND", "/things/u1/", body, headers: Depth("infinity"));

        response.Status.Should().Be(403);
        response.Xml.Child(DavNames.D.PropFindFiniteDepth).Should().NotBeNull();
    }

    // ---- PROPFIND: targets ----

    /// <summary>
    /// Apple propfind.xml "calendar" 5–7 and CardDAV propfind.xml 5–7 (P-05–P-08); Cyrus propfind_nonexistent_resource
    /// (D19); sabre/dav HttpGetTest testGet404AsWell (W27); sabre testCalculateUriBreakout (W25): a missing target, a
    /// missing parent, a child of a member, or a path leaving the user's tree answers a plain 404, never an empty 207.
    /// </summary>
    [Theory]
    [InlineData("/things/u1/foobar/")]
    [InlineData("/things/u1/foobar/foo.ics")]
    [InlineData("/things/u1/foobar/example/")]
    [InlineData("/things/u1/shifts.mine/missing.txt")]
    [InlineData("/things/u1/shifts.mine/a.txt/sub")]
    [InlineData("/principals/u1/extra/")]
    [InlineData("/nothing/")]
    [InlineData("/things/u1/../u2/")]
    [InlineData("/things/u1/%2e%2e/u2/")]
    public async Task PropFindOnAMissingTargetOrParent_Answers404WithoutAMultistatus(string path) {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("PROPFIND", path, DavTestHarness.PropFind("D:resourcetype"), headers: Depth("1"));

        response.Status.Should().Be(404);
        response.Body.Should().NotContain("multistatus");
    }

    /// <summary>
    /// litmus props.c propfind_d0 (P-21); dav4jvm ResponseParserTest "collection href gets trailing slash" (RP-22): a
    /// collection addressed without its trailing slash answers with its canonical href, which ends in one.
    /// </summary>
    [Fact]
    public async Task ACollectionAddressedWithoutTrailingSlash_AnswersItsHrefWithOne() {
        await using var harness = await WithMembersAsync("a");

        var collection = await harness.SendAsync("PROPFIND", "/things/u1/shifts.mine", DavTestHarness.PropFind("D:resourcetype"), headers: Depth("0"));
        var principal = await harness.SendAsync("PROPFIND", "/principals/u1", DavTestHarness.PropFind("D:resourcetype"), headers: Depth("0"));

        collection.Responses.Keys.Should().Equal(CollectionHref);
        collection.Prop(CollectionHref, DavNames.D.ResourceType)!.Child(DavNames.D.Collection).Should().NotBeNull();
        principal.Responses.Keys.Should().Equal("/dav/principals/u1/");
    }

    /// <summary>
    /// Apple errors.xml "Root resource" 4 (X-11), sabre/dav HttpGetTest testGetDoubleSlash (W26), Radicale
    /// test_sanitized_path (D03): empty segments are normalised away (Radicale redirects instead; Elarion serves the
    /// same resource), never a 5xx.
    /// </summary>
    [Fact]
    public async Task DoubleSlashes_AddressTheSameResource() {
        await using var harness = await WithMembersAsync("a");

        var get = await harness.SendAsync("GET", "//things//u1//shifts.mine//a.txt");
        var root = await harness.SendAsync("PROPFIND", "//", DavTestHarness.PropFind("D:current-user-principal"), headers: Depth("0"));

        get.Status.Should().Be(200);
        get.Body.Should().Be("A");
        root.Status.Should().Be(207);
        root.Responses.Keys.Should().Equal("/dav/");
    }

    // ---- PROPFIND: propname, allprop, include, body shapes ----

    /// <summary>Apple propfind.xml "prop names" 2 (P-09), Radicale test_propfind_propname (D07): propname at Depth 1 lists the members' names too, without values.</summary>
    [Fact]
    public async Task PropNameAtDepth1_ListsTheMembersPropertyNamesWithoutValues() {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("PROPFIND", Collection, "<D:propfind xmlns:D=\"DAV:\"><D:propname/></D:propfind>", headers: Depth("1"));

        response.Responses.Keys.Should().BeEquivalentTo(CollectionHref, CollectionHref + "a.txt");
        var etag = response.Prop(CollectionHref + "a.txt", DavNames.D.GetETag);
        etag.Should().NotBeNull();
        etag!.Text.Should().BeEmpty();
        response.Prop(CollectionHref + "a.txt", DavNames.D.GetContentLength)!.Text.Should().BeEmpty();
        response.Prop(CollectionHref, DavNames.D.SyncToken)!.Text.Should().BeEmpty();
    }

    /// <summary>
    /// sabre/dav PropFindTest testAllPropDefaults (W05), Xandikos test_rfc4918_9_1_propfind_allprop (D10): allprop never
    /// reports a property as missing.
    /// </summary>
    [Fact]
    public async Task AllProp_ReportsNo404PropStat() {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("PROPFIND", Collection, "<D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>", headers: Depth("1"));

        response.Status.Should().Be(207);
        PropStats(response, "404").Should().BeEmpty();
        response.Prop(CollectionHref + "a.txt", DavNames.D.GetETag).Should().NotBeNull();
    }

    /// <summary>RFC 4918 §9.1 include; Apple propfind.xml "prop all" (P-10): allprop leaves out sync-token unless an include names it.</summary>
    [Fact]
    public async Task AllPropWithInclude_AddsTheIncludedProperty() {
        await using var harness = await WithMembersAsync("a");

        var plain = await harness.SendAsync("PROPFIND", Collection, "<D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>", headers: Depth("0"));
        var included = await harness.SendAsync("PROPFIND", Collection,
            "<D:propfind xmlns:D=\"DAV:\"><D:allprop/><D:include><D:sync-token/></D:include></D:propfind>", headers: Depth("0"));

        plain.Prop(CollectionHref, DavNames.D.SyncToken).Should().BeNull();
        included.Prop(CollectionHref, DavNames.D.SyncToken)!.Text.Should().StartWith(DavSyncToken.Prefix);
        included.Prop(CollectionHref, DavNames.D.ResourceType).Should().NotBeNull();
    }

    /// <summary>
    /// Apple propfind.xml "prop errors" 1 (P-11): allprop and propname together break the propfind content model
    /// (RFC 4918 §14.20), so the request is a 400, not an answer to one of them.
    /// </summary>
    [Fact]
    public async Task APropFindWithAllPropAndPropName_Answers400() {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("PROPFIND", Collection, "<D:propfind xmlns:D=\"DAV:\"><D:allprop/><D:propname/></D:propfind>",
            headers: Depth("0"));

        response.Status.Should().Be(400);
    }

    /// <summary>
    /// Apple propfind.xml "prop errors" 2 and CardDAV propfind.xml "prop errors" 2 (P-12): a propfind with no prop,
    /// propname or allprop child (only an unknown one, or none) is invalid; it is not an allprop.
    /// </summary>
    [Theory]
    [InlineData("<D:propfind xmlns:D=\"DAV:\"><D:undefined/></D:propfind>")]
    [InlineData("<D:propfind xmlns:D=\"DAV:\"/>")]
    public async Task APropFindWithoutAValidChild_Answers400(string body) {
        await using var harness = await WithMembersAsync("a");

        (await harness.SendAsync("PROPFIND", Collection, body, headers: Depth("0"))).Status.Should().Be(400);
    }

    /// <summary>litmus props.c propextended (P-17), RFC 4918 §17: an unknown element beside a valid one is ignored.</summary>
    [Theory]
    [InlineData("<D:propfind xmlns:D=\"DAV:\"><D:foobar/><D:allprop/></D:propfind>")]
    [InlineData("<D:propfind xmlns:D=\"DAV:\" xmlns:X=\"urn:x\"><X:foobar/><D:prop><D:displayname/><D:resourcetype/></D:prop></D:propfind>")]
    public async Task AnUnknownElementBesideAValidOne_IsIgnored(string body) {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("PROPFIND", Collection, body, headers: Depth("0"));

        response.Status.Should().Be(207);
        response.ContentType.Should().StartWith("application/xml");
        response.Prop(CollectionHref, DavNames.D.ResourceType).Should().NotBeNull();
    }

    /// <summary>litmus props.c propfind_invalid2 (P-16): undeclaring a prefix (<c>xmlns:bar=""</c>) is illegal in XML Namespaces 1.0.</summary>
    [Fact]
    public async Task AnUndeclaredPrefix_Answers400() {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("PROPFIND", Collection,
            "<D:propfind xmlns:D=\"DAV:\"><D:prop><bar:foo xmlns:bar=\"\"/></D:prop></D:propfind>", headers: Depth("0"));

        response.Status.Should().Be(400);
    }

    /// <summary>
    /// litmus props.c propwformed and propnullns (P-18), sabre/dav ResponseTest testSerializeEmptyNamespace (W16):
    /// an unknown property without a namespace, or in a namespace without a fixed prefix, comes back in the 404 propstat
    /// under its own name; the no-namespace element must not land in <c>DAV:</c> or another default namespace.
    /// </summary>
    [Fact]
    public async Task UnknownPropertiesWithoutANamespaceOrInAForeignOne_ComeBackUnderTheirOwnNames() {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("PROPFIND", Collection,
            "<propfind xmlns='DAV:'><prop><nonamespace xmlns=''/><displayname/><L:foo xmlns:L='http://example.com/neon/litmus/'/></prop></propfind>",
            headers: Depth("0"));

        response.Status.Should().Be(207);
        response.IsMissing(CollectionHref, new DavName("", "nonamespace")).Should().BeTrue();
        response.IsMissing(CollectionHref, new DavName("http://example.com/neon/litmus/", "foo")).Should().BeTrue();
        response.IsMissing(CollectionHref, new DavName(DavNames.Dav, "nonamespace")).Should().BeFalse();
        response.Prop(CollectionHref, DavNames.D.DisplayName).Should().NotBeNull();
    }

    /// <summary>litmus props.c prophighunicode/propget (P-19): a non-BMP character (a surrogate pair in .NET) in a property's namespace round-trips well-formed.</summary>
    [Fact]
    public async Task ANonBmpCharacterInAPropertyNamespace_RoundTripsWellFormed() {
        await using var harness = await WithMembersAsync("a");
        const string ns = "http://example.com/\U00010000/";

        var response = await harness.SendAsync("PROPFIND", Collection,
            $"<D:propfind xmlns:D=\"DAV:\"><D:prop><H:high xmlns:H=\"{ns}\"/></D:prop></D:propfind>", headers: Depth("0"));

        response.Status.Should().Be(207);
        response.IsMissing(CollectionHref, new DavName(ns, "high")).Should().BeTrue();
    }

    /// <summary>
    /// Cyrus get_control_char / control_chars_repaired (H04), vdirsyncer test_xml_specialchars (RP-29): a C0 control
    /// character in a value the server writes (here a collection's display name) must not make the multistatus
    /// ill-formed XML 1.0; it is dropped or escaped, and the answer stays a 207.
    /// </summary>
    [Fact]
    public async Task AControlCharacterInAWrittenValue_KeepsTheMultistatusWellFormed() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine", "Bad\u0001Name\u000c");

        var response = await harness.SendAsync("PROPFIND", Collection, DavTestHarness.PropFind("D:displayname"), headers: Depth("0"));

        response.Status.Should().Be(207);
        response.Prop(CollectionHref, DavNames.D.DisplayName)!.Text.Should().NotContain("\u0001").And.Contain("Bad");
    }

    // ---- PROPFIND: Brief and Prefer ----

    /// <summary>
    /// Apple brief.xml "regular prop finds" 1–4 (P-22) and prefer.xml "minimal: propfind" 1–4 (P-24), sabre/dav
    /// HTTPPreferParsingTest testBrief/testParseWeirdValue (W62, W63): only <c>Brief: t</c> and <c>return=minimal</c>
    /// drop the 404 propstat; <c>Brief: f</c>, an unknown Brief value and an unknown preference behave as absent.
    /// </summary>
    [Theory]
    [InlineData("Brief", "t", true)]
    [InlineData("Brief", "T", true)]
    [InlineData("Brief", "f", false)]
    [InlineData("Brief", "bad", false)]
    [InlineData("Prefer", "return=minimal", true)]
    [InlineData("Prefer", "x-foo", false)]
    [InlineData("Prefer", "BOOOH", false)]
    public async Task BriefAndPrefer_DropThe404PropStatOnlyWhenTheyAskForIt(string header, string value, bool minimal) {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("PROPFIND", Collection, DavTestHarness.PropFind("D:resourcetype", "D:bogus"),
            headers: Headers(("Depth", "0"), (header, value)));

        response.Status.Should().Be(207);
        response.Prop(CollectionHref, DavNames.D.ResourceType).Should().NotBeNull();
        response.IsMissing(CollectionHref, new DavName(DavNames.Dav, "bogus")).Should().Be(!minimal);
    }

    /// <summary>Apple brief.xml "regular prop finds" 5 (P-23): minimal with only unknown properties still answers a valid response for the resource.</summary>
    [Fact]
    public async Task MinimalWithOnlyUnknownProperties_AnswersAResponseWithoutAny404() {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("PROPFIND", Collection, DavTestHarness.PropFind("D:bogus"), headers: Headers(("Depth", "0"), ("Brief", "t")));

        response.Status.Should().Be(207);
        response.Responses.Keys.Should().Equal(CollectionHref);
        PropStats(response, "404").Should().BeEmpty();
    }

    /// <summary>
    /// Apple prefer.xml "noroot: propfind" 1–3 (P-25): Elarion does not implement <c>Prefer: depth-noroot</c>, so it is
    /// ignored and the collection is still answered. Pinned.
    /// </summary>
    [Fact]
    public async Task PreferDepthNoRoot_IsIgnored_AndTheCollectionIsStillAnswered() {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("PROPFIND", Collection, DavTestHarness.PropFind("D:getetag"),
            headers: Headers(("Depth", "1"), ("Prefer", "depth-noroot")));

        response.Responses.Keys.Should().BeEquivalentTo(CollectionHref, CollectionHref + "a.txt");
    }

    // ---- PROPFIND: properties by resource kind ----

    /// <summary>
    /// Apple propfind.xml home 4, calendar 4 and CardDAV propfind.xml 4 (P-04) expect getcontentlength 0 on a
    /// collection; Elarion defines it only on members, so a collection answers it in a 404 propstat. Pinned.
    /// </summary>
    [Fact]
    public async Task GetContentLength_IsAMemberProperty_AndMissingOnCollections() {
        await using var harness = await WithMembersAsync("a");

        var collection = await harness.SendAsync("PROPFIND", Collection, DavTestHarness.PropFind("D:getcontentlength"), headers: Depth("0"));
        var home = await harness.SendAsync("PROPFIND", "/things/u1/", DavTestHarness.PropFind("D:getcontentlength"), headers: Depth("0"));
        var member = await harness.SendAsync("PROPFIND", Collection + "a.txt", DavTestHarness.PropFind("D:getcontentlength"), headers: Depth("0"));

        collection.IsMissing(CollectionHref, DavNames.D.GetContentLength).Should().BeTrue();
        home.IsMissing("/dav/things/u1/", DavNames.D.GetContentLength).Should().BeTrue();
        member.Prop(CollectionHref + "a.txt", DavNames.D.GetContentLength)!.Text.Should().Be("1");
    }

    /// <summary>
    /// Apple sync-report.xml "support-report-set/sync-token property" 1–3 and CardDAV 1–3 (P-27–P-29, S-17); sabre/dav
    /// Sync PluginTest testGetSyncToken/testSyncNoSyncCollection (S02, S09); Xandikos test_report_not_implemented (S17):
    /// a resource that cannot be synced neither advertises sync-collection nor has a sync-token, and the report there is
    /// 403 supported-report; a collection has both, and they agree.
    /// </summary>
    [Theory]
    [InlineData("/", "/dav/")]
    [InlineData("/principals/", "/dav/principals/")]
    [InlineData("/principals/u1/", "/dav/principals/u1/")]
    [InlineData("/things/u1/", "/dav/things/u1/")]
    public async Task AResourceThatCannotBeSynced_AdvertisesNoSyncAndRefusesTheReport(string path, string href) {
        await using var harness = await WithMembersAsync("a");

        var properties = await harness.SendAsync("PROPFIND", path, DavTestHarness.PropFind("D:supported-report-set", "D:sync-token"), headers: Depth("0"));
        var report = await harness.SendAsync("REPORT", path, DavTestHarness.SyncCollection(null));

        properties.IsMissing(href, DavNames.D.SyncToken).Should().BeTrue();
        var reports = properties.Prop(href, DavNames.D.SupportedReportSet);
        (reports is null || !reports.Body().Contains("sync-collection", StringComparison.Ordinal)).Should().BeTrue();
        report.Status.Should().Be(403);
        report.Xml.Child(DavNames.D.SupportedReport).Should().NotBeNull();
    }

    /// <summary>The collection half of the previous cases (P-28, P-29; sabre S01, Radicale D17): advertised, with a token equal to the initial sync's.</summary>
    [Fact]
    public async Task ACollection_AdvertisesSyncCollection_AndItsSyncTokenEqualsTheInitialSyncs() {
        await using var harness = await WithMembersAsync("a");

        var properties = await harness.SendAsync("PROPFIND", Collection, DavTestHarness.PropFind("D:supported-report-set", "D:sync-token"), headers: Depth("0"));
        var initial = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(null));

        properties.Prop(CollectionHref, DavNames.D.SupportedReportSet)!.Body().Should().Contain("sync-collection");
        properties.Prop(CollectionHref, DavNames.D.SyncToken)!.Text.Should().Be(initial.SyncToken);
    }

    // ---- href encoding ----

    /// <summary>
    /// Apple encodedURIs.xml calendar resource 2 (X-14), sabre/dav ResponseTest testSerializeUrlencoding (W13), litmus
    /// basic.c put_get_utf8_segment (X-13), Cyrus utf8_url (D22), Radicale test_move_unicode (D24), vdirsyncer
    /// test_dav_unicode_href (CT-05): member names with a space, a UTF-8 character and a non-BMP character are answered
    /// percent-encoded as UTF-8, and the href GETs back the member.
    /// </summary>
    [Theory]
    [InlineData("a b", "a%20b.txt")]
    [InlineData("€", "%E2%82%AC.txt")]
    [InlineData("event😀1", "event%F0%9F%98%801.txt")]
    [InlineData("ät град", "%C3%A4t%20%D0%B3%D1%80%D0%B0%D0%B4.txt")]
    public async Task AMemberName_IsAnsweredPercentEncoded_AndItsHrefGetsItBack(string uid, string encoded) {
        await using var harness = await WithMembersAsync(uid);

        var listing = await harness.SendAsync("PROPFIND", Collection, DavTestHarness.PropFind("D:getetag"), headers: Depth("1"));
        var get = await harness.SendAsync("GET", Collection + encoded);
        var lowerCase = await harness.SendAsync("GET", Collection + encoded.ToLowerInvariant());

        listing.Responses.Keys.Should().Contain(CollectionHref + encoded);
        get.Status.Should().Be(200);
        get.Body.Should().Be(uid.ToUpperInvariant());
        lowerCase.Status.Should().Be(200);
    }

    /// <summary>
    /// Apple encodedURIs.xml double-encoded 3, 9 (M-09, Q-24), vdirsyncer test_normalize_href (RP-27), python-caldav
    /// TestASlashInTheName (RP-28): a member literally named <c>3%20a.txt</c> is answered as <c>3%2520a.txt</c>, and a
    /// request path is decoded exactly once, so it never finds <c>3 a.txt</c> instead.
    /// </summary>
    [Fact]
    public async Task ALiteralPercentInAName_IsEncodedAsPercent25_AndDecodedExactlyOnce() {
        await using var harness = DavTestHarness.Create();
        harness.Protocol.Show("u1", "shifts", "mine");
        await harness.PublishAsync("shifts", "mine", "3 a", "space");
        await harness.PublishAsync("shifts", "mine", "3%20a", "literal");

        var listing = await harness.SendAsync("PROPFIND", Collection, DavTestHarness.PropFind("D:getetag"), headers: Depth("1"));
        var literal = await harness.SendAsync("GET", Collection + "3%2520a.txt");
        var space = await harness.SendAsync("GET", Collection + "3%20a.txt");

        listing.Responses.Keys.Should().Contain([CollectionHref + "3%2520a.txt", CollectionHref + "3%20a.txt"]);
        literal.Body.Should().Be("literal");
        space.Body.Should().Be("space");
    }

    /// <summary>
    /// python-caldav test_xml_parsing (RP-05), dav4jvm "resolveHref keeps percent-encoding" (RP-24, RP-25),
    /// caldav-server-tester "robur takes a %40 href" (MG-06): <c>@</c> is answered as <c>%40</c>, and a request with
    /// either spelling reaches the member.
    /// </summary>
    [Fact]
    public async Task AnAtSign_IsAnsweredAsPercent40_AndBothSpellingsReachTheMember() {
        await using var harness = await WithMembersAsync("x@y");

        var listing = await harness.SendAsync("PROPFIND", Collection, DavTestHarness.PropFind("D:getetag"), headers: Depth("1"));

        listing.Responses.Keys.Should().Contain(CollectionHref + "x%40y.txt");
        (await harness.SendAsync("GET", Collection + "x%40y.txt")).Status.Should().Be(200);
        (await harness.SendAsync("GET", Collection + "x@y.txt")).Status.Should().Be(200);
    }

    // ---- GET, HEAD and If-None-Match ----

    /// <summary>
    /// RFC 9110 §13.1.2 (weak comparison for If-None-Match, a gap Apple and litmus do not test); Xandikos ETagMatchesTests
    /// (D34); sabre/dav ServerPreconditionsTest testIfNoneMatchCorrectEtagAsGet/WrongEtagMultiple (W47, W49) and
    /// testIfMatchEvolutionEtag (W43, Evolution sends <c>\"etag\"</c> with escaped quotes): the validators that match the
    /// stored ETag answer 304, the others 200.
    /// </summary>
    [Theory]
    [InlineData("{0}", 304)]
    [InlineData("W/{0}", 304)]
    [InlineData("*", 304)]
    [InlineData("\"x\", {0}", 304)]
    [InlineData("\"x\",{0}", 304)]
    [InlineData("\"x\", \"y\"", 200)]
    [InlineData("\"x\" {0}", 200)]
    [InlineData("{1}", 304)]
    public async Task IfNoneMatch_AnswersNotModifiedOnlyForAMatchingValidator(string format, int expected) {
        await using var harness = await WithMembersAsync("a");
        var etag = DavETag.Compute("A"u8);
        var evolution = etag.Replace("\"", "\\\"", StringComparison.Ordinal);

        var response = await harness.SendAsync("GET", Collection + "a.txt",
            headers: Headers(("If-None-Match", string.Format(System.Globalization.CultureInfo.InvariantCulture, format, etag, evolution))));

        response.Status.Should().Be(expected);
        if (expected == 304) response.Headers["ETag"].Should().Be(etag);
    }

    /// <summary>litmus common.c get_etag (G-10), sabre/dav HttpHeadTest testHEAD (W30): HEAD carries the same ETag and type as GET.</summary>
    [Fact]
    public async Task Head_CarriesTheSameETagAndContentTypeAsGet() {
        await using var harness = await WithMembersAsync("a");

        var get = await harness.SendAsync("GET", Collection + "a.txt");
        var head = await harness.SendAsync("HEAD", Collection + "a.txt");

        head.Status.Should().Be(200);
        head.Headers["ETag"].Should().Be(get.Headers["ETag"]);
        head.ContentType.Should().Be(get.ContentType);
    }

    /// <summary>
    /// sabre/dav HttpGetTest testGetCollection (W28, 501) and HttpHeadTest testHEADCollection (W31, 200), Apple get.xml
    /// "GET on collections" (G-02): Elarion serves no body for a collection (the ICS feed is the calendar's GET), and
    /// answers GET and HEAD there with 405 and an Allow header alike. Pinned.
    /// </summary>
    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/things/u1/")]
    [InlineData("GET", Collection)]
    [InlineData("HEAD", "/principals/u1/")]
    [InlineData("HEAD", Collection)]
    public async Task GetOrHeadOnACollection_Answers405WithAllow(string method, string path) {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync(method, path);

        response.Status.Should().Be(405);
        response.Headers["Allow"].Should().Be(DavDispatcher.AllowedMethods);
    }

    // ---- Refused writes ----

    /// <summary>
    /// Apple errors.xml "Root resource" 1–3 and PUT 25/26, CardDAV errors.xml 25/26 (W-01, W-02); litmus protected.c
    /// (W-11–W-15); Radicale test_delete_root_collection (W09); Xandikos test_lock_not_allowed (W01), test_post_* (W10);
    /// sabre/dav Sync testIfConditions (W56–W58); vdirsyncer test_wrong_etag (WR-02); Apple prefer.xml PUT 5 (W-16):
    /// every write method on every resource kind the user sees is refused with 403 need-privileges, before any
    /// precondition (If-Match, If, Destination, Overwrite) is evaluated, and never with 401.
    /// </summary>
    [Theory]
    [InlineData("DELETE", "/")]
    [InlineData("COPY", "/")]
    [InlineData("MOVE", "/")]
    [InlineData("PUT", "/principals/")]
    [InlineData("PROPPATCH", "/principals/u1/")]
    [InlineData("MKCOL", "/things/u1/")]
    [InlineData("PUT", "/things/u1/")]
    [InlineData("DELETE", "/things/u1/")]
    [InlineData("PUT", Collection)]
    [InlineData("DELETE", Collection)]
    [InlineData("MKCOL", Collection + "sub/")]
    [InlineData("MKCALENDAR", Collection)]
    [InlineData("LOCK", Collection)]
    [InlineData("LOCK", Collection + "a.txt")]
    [InlineData("UNLOCK", Collection + "a.txt")]
    [InlineData("ACL", Collection + "a.txt")]
    [InlineData("POST", Collection)]
    [InlineData("POST", Collection + "a.txt")]
    [InlineData("PATCH", Collection + "a.txt")]
    [InlineData("BIND", Collection)]
    [InlineData("COPY", Collection + "a.txt")]
    [InlineData("MOVE", Collection + "a.txt")]
    public async Task EveryWriteMethodOnEveryVisibleResource_Answers403NeedPrivilegesBeforeAnyPrecondition(string method, string path) {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync(method, path, "<D:propertyupdate xmlns:D=\"DAV:\"/>", headers: Headers(
            ("If-Match", "\"does-not-match\""), ("If", "(<opaquelocktoken:foo>)"), ("Destination", "http://example.com/dav" + Collection + "copy.txt"),
            ("Overwrite", "F")));

        response.Status.Should().Be(403);
        response.Xml.Child(DavNames.D.NeedPrivileges)!.Child(DavNames.D.Resource)!.Child(DavNames.D.Href)!.Text.Should().StartWith("/dav/");
        (await harness.SendAsync("GET", Collection + "a.txt")).Body.Should().Be("A");
    }

    /// <summary>
    /// Apple delete.xml 3 and CardDAV errors.xml 5, litmus delete_null (W-03), Apple proppatch.xml 6 (W-07): a write to
    /// a missing member, or to a missing collection in the user's home, is refused with 403 (the privilege answer, no
    /// existence leak) rather than 404; Elarion's choice, pinned. Below a missing parent it is 404.
    /// </summary>
    [Theory]
    [InlineData("DELETE", Collection + "missing.txt", 403)]
    [InlineData("PUT", Collection + "new.txt", 403)]
    [InlineData("PROPPATCH", "/things/u1/nope/", 403)]
    [InlineData("MKCALENDAR", "/things/u1/nope/", 403)]
    [InlineData("PUT", Collection + "missing/sub.txt", 404)]
    [InlineData("PUT", "/things/u1/nope/sub.txt", 404)]
    [InlineData("PUT", Collection + "a.txt/sub", 404)]
    public async Task AWriteToAMissingTarget_Answers403_AndBelowAMissingParent404(string method, string path, int expected) {
        await using var harness = await WithMembersAsync("a");

        (await harness.SendAsync(method, path, "x")).Status.Should().Be(expected);
    }

    /// <summary>
    /// litmus http.c expect100 (X-15), Radicale test_add_event_exceed_size (W04), Apple errors.xml PUT 1–7 (W-09): a
    /// refused write never reads its body, so a large or slow upload is answered at once and no body parser runs.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARefusedWrite_ReadsNoByteOfItsBody(bool declaredLength) {
        await using var harness = await WithMembersAsync("a");
        var body = new CountingStream(100L * 1024 * 1024);

        var response = await harness.SendAsync("PUT", Collection + "a.txt", bodyStream: body, contentLength: declaredLength ? body.Length : null);

        response.Status.Should().Be(403);
        body.BytesRead.Should().Be(0);
    }

    // ---- Hostile input on a REPORT ----

    /// <summary>python-caldav TestXMLEntityHardening (RP-30) and the litmus hostile-XML set (X-16), on a REPORT: a DTD is refused.</summary>
    [Fact]
    public async Task ADocumentTypeDeclarationInAReport_Answers400() {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("REPORT", Collection,
            "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"y\">]><D:sync-collection xmlns:D=\"DAV:\"><D:sync-token>&e;</D:sync-token>"
            + "<D:sync-level>1</D:sync-level><D:prop/></D:sync-collection>");

        response.Status.Should().Be(400);
    }

    /// <summary>The coverage gap Apple's catalogue names (an empty REPORT body): answered 400, never 500.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n")]
    public async Task AnEmptyReportBody_Answers400(string? body) {
        await using var harness = await WithMembersAsync("a");

        (await harness.SendAsync("REPORT", Collection, body)).Status.Should().Be(400);
    }

    // ---- sync-collection ----

    /// <summary>
    /// Apple sync-report.xml "empty token - no props" 1 (S-01), sabre/dav PropTest testDeserializeEmpty (W12): an empty
    /// prop is legal; the initial sync lists every member with its href and a token.
    /// </summary>
    [Fact]
    public async Task SyncCollection_WithAnEmptyProp_ListsEveryMemberAndAToken() {
        await using var harness = await WithMembersAsync("a", "b");

        var response = await harness.SendAsync("REPORT", Collection,
            "<D:sync-collection xmlns:D=\"DAV:\"><D:sync-token/><D:sync-level>1</D:sync-level><D:prop/></D:sync-collection>");

        response.Status.Should().Be(207);
        response.Responses.Keys.Should().BeEquivalentTo(CollectionHref + "a.txt", CollectionHref + "b.txt");
        response.SyncToken.Should().StartWith(DavSyncToken.Prefix);
        PropStats(response, "404").Should().BeEmpty();
    }

    /// <summary>
    /// DAVx5 (dav4jvm DavCollectionTest "reportChanges sends proper request", SY-01) sends Depth 0 and a self-closed
    /// <c>&lt;sync-token /&gt;</c>; Apple sync-report.xml "sync-level" 7–9 (S-02) sends Depth 0, 1 and none; sabre/dav
    /// testSubsequentSyncSyncCollectionDepthFallBack (S07) omits sync-level; Xandikos test_report_unknown_tag (S16) adds
    /// an unknown element. The Depth header does not change a sync-collection.
    /// </summary>
    [Theory]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData(null, true)]
    [InlineData("1", false)]
    public async Task SyncCollection_IgnoresTheDepthHeader_AsDavx5SendsIt(string? depth, bool withLevel) {
        await using var harness = await WithMembersAsync("a", "b");
        var body = "<D:sync-collection xmlns:D=\"DAV:\" xmlns:X=\"urn:x\"><D:sync-token />" + (withLevel ? "<D:sync-level>1</D:sync-level>" : "")
                   + "<X:unknown/><D:prop><D:getetag /></D:prop></D:sync-collection>";

        var response = await harness.SendAsync("REPORT", Collection, body, headers: depth is null ? null : Depth(depth));

        response.Status.Should().Be(207);
        response.Responses.Keys.Should().BeEquivalentTo(CollectionHref + "a.txt", CollectionHref + "b.txt");
        response.Prop(CollectionHref + "a.txt", DavNames.D.GetETag)!.Text.Should().Be(DavETag.Compute("A"u8));
    }

    /// <summary>
    /// Apple sync-report.xml "sync-level" 13 (S-04) and "limited reports" 1 (S-05, sync-level 0): a sync-level that is
    /// neither 1 nor infinity is a bad request (400), not the infinity precondition.
    /// </summary>
    [Theory]
    [InlineData("bogus")]
    [InlineData("0")]
    public async Task SyncCollection_WithAnInvalidSyncLevel_Answers400(string level) {
        await using var harness = await WithMembersAsync("a");

        (await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(null, level: level))).Status.Should().Be(400);
    }

    /// <summary>
    /// DAVx5 sends <c>infinite</c> (dav4jvm "reportChanges infiniteDepth", SY-02); Xandikos test_report_invalid_sync_level
    /// (S14) answers 400, sabre (S05) accepts it as 1. Elarion reads it as the infinity DAVx5 means and answers the level-1
    /// server's precondition, 403 sync-traversal-supported. Pinned.
    /// </summary>
    [Fact]
    public async Task SyncCollection_WithLevelInfinite_Answers403SyncTraversalSupported() {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(null, level: "infinite"));

        response.Status.Should().Be(403);
        response.Xml.Child(DavNames.D.SyncTraversalSupported).Should().NotBeNull();
    }

    /// <summary>
    /// Apple sync-report.xml "valid token" 1 (S-06, <c>null</c>), sabre/dav testSyncInvalidToken (S10, right prefix with
    /// a bad suffix), caldav-server-tester TestSyncTokenInvalidTokenGrading (SY-19): a garbage token is 403
    /// valid-sync-token, never a parse failure.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("urn:elarion:sync:{0}:abc")]
    [InlineData("urn:elarion:sync:{0}:-1")]
    [InlineData("urn:elarion:sync:not-a-guid:1")]
    [InlineData("http://example.invalid/never-issued")]
    public async Task SyncCollection_WithAGarbageToken_Answers403ValidSyncToken(string format) {
        await using var harness = await WithMembersAsync("a");
        var token = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, TestThingsProtocol.Address("shifts", "mine").Id);

        var response = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(token));

        response.Status.Should().Be(403);
        response.Xml.Child(DavNames.D.ValidSyncToken).Should().NotBeNull();
    }

    /// <summary>Radicale _report_sync_token (S09): a token in CDATA with surrounding whitespace reads like plain text.</summary>
    [Fact]
    public async Task SyncCollection_ReadsATokenInCDataWithWhitespace() {
        await using var harness = await WithMembersAsync("a");
        var token = (await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(null))).SyncToken;
        await harness.PublishAsync("shifts", "mine", "b", "B");

        var response = await harness.SendAsync("REPORT", Collection,
            $"<D:sync-collection xmlns:D=\"DAV:\"><D:sync-token>\n  <![CDATA[{token}]]>  \n</D:sync-token><D:sync-level>1</D:sync-level><D:prop><D:getetag/></D:prop></D:sync-collection>");

        response.Status.Should().Be(207);
        response.Responses.Keys.Should().Equal(CollectionHref + "b.txt");
    }

    /// <summary>
    /// Apple sync-report.xml "diff token" 3, 4 and "diff token - props" (S-08, S-09, S-12); Radicale
    /// test_report_sync_collection_create_delete and _delete (S03, S04); caldav-server-tester
    /// test_a_created_and_deleted_member_left_out (SY-18): a member created and removed since the token is never
    /// reported as present; a removed member is a response with a 404 status and no propstat.
    /// </summary>
    [Fact]
    public async Task SyncCollection_AMemberCreatedAndRemovedSinceTheToken_IsATombstoneWithoutPropStat() {
        await using var harness = await WithMembersAsync("a");
        var before = (await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(null))).SyncToken;
        await harness.PublishAsync("shifts", "mine", "x", "X");
        var between = (await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(null))).SyncToken;
        await harness.RemoveAsync("shifts", "mine", "x");

        var fromBefore = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(before, props: "D:getcontenttype"));
        var fromBetween = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(between, props: "D:getcontenttype"));

        foreach (var delta in new[] { fromBefore, fromBetween }) {
            delta.Status.Should().Be(207);
            delta.Responses.Keys.Should().Equal(CollectionHref + "x.txt");
            var removed = delta.Responses[CollectionHref + "x.txt"];
            removed.Child(DavNames.D.Status)!.Text.Should().Contain(" 404 ");
            removed.ChildrenNamed(DavNames.D.Propstat).Should().BeEmpty();
        }
    }

    /// <summary>
    /// Radicale test_report_sync_collection_modify_undo (S05): a member changed and restored to identical bytes since the
    /// token is reported once, as present.
    /// </summary>
    [Fact]
    public async Task SyncCollection_AMemberChangedAndRestored_IsReportedOnceAsPresent() {
        await using var harness = await WithMembersAsync("a");
        var token = (await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(null))).SyncToken;
        await harness.PublishAsync("shifts", "mine", "a", "changed");
        await harness.PublishAsync("shifts", "mine", "a", "A");

        var delta = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(token));

        delta.Responses.Keys.Should().Equal(CollectionHref + "a.txt");
        delta.Prop(CollectionHref + "a.txt", DavNames.D.GetETag)!.Text.Should().Be(DavETag.Compute("A"u8));
    }

    /// <summary>
    /// Apple sync-report.xml "empty inbox" 1 (S-15), Radicale test_report_sync_collection_add (S01): an empty collection
    /// answers no member and a token, and the next delta from that token works.
    /// </summary>
    [Fact]
    public async Task SyncCollection_OnAnEmptyCollection_AnswersAUsableToken() {
        await using var harness = await WithMembersAsync();

        var initial = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(null));
        await harness.PublishAsync("shifts", "mine", "a", "A");
        var delta = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(initial.SyncToken));

        initial.Status.Should().Be(207);
        initial.Responses.Should().BeEmpty();
        initial.SyncToken.Should().NotBeNullOrEmpty();
        delta.Status.Should().Be(207);
        delta.Responses.Keys.Should().Equal(CollectionHref + "a.txt");
    }

    /// <summary>
    /// Apple sync-report.xml "delete/create calendar" 2–4 (S-14): in Elarion a collection is removed and re-added by its
    /// source, while the store keeps its content and counter (the collection id derives from kind, source and key). So
    /// while the source hides it the report answers 404, and afterwards a token of the earlier listing stays valid and
    /// receives exactly what changed meanwhile. Pinned (the catalogue proposed 403 valid-sync-token).
    /// </summary>
    [Fact]
    public async Task SyncCollection_AfterTheCollectionIsHiddenAndShownAgain_TheOldTokenReceivesWhatChangedMeanwhile() {
        await using var harness = await WithMembersAsync("a");
        var token = (await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(null))).SyncToken;
        var listed = harness.Protocol.Visible["u1"].ToList();
        harness.Protocol.Visible["u1"].Clear();

        var hidden = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(token));
        await harness.PublishAsync("shifts", "mine", "b", "B");
        harness.Protocol.Visible["u1"].AddRange(listed);
        var shown = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(token));

        hidden.Status.Should().Be(404);
        shown.Status.Should().Be(207);
        shown.Responses.Keys.Should().Equal(CollectionHref + "b.txt");
    }

    /// <summary>
    /// sabre/dav testSubsequentSyncSyncCollectionLimit (S06), Xandikos test_report_with_limit (S15), DAVx5 "reportChanges
    /// truncated" (SY-05), python-caldav TestSyncCollectionTruncated (SY-08): a truncated delta holding a removal and a
    /// change answers 507 for the request-URI and a token from which the rest follows without loss or repetition.
    /// </summary>
    [Fact]
    public async Task SyncCollection_ATruncatedDeltaWithARemoval_ContinuesWithoutLossFromItsToken() {
        await using var harness = await WithMembersAsync("a", "b", "c");
        var token = (await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(null))).SyncToken;
        await harness.RemoveAsync("shifts", "mine", "a");
        await harness.PublishAsync("shifts", "mine", "b", "B2");
        await harness.RemoveAsync("shifts", "mine", "c");
        await harness.PublishAsync("shifts", "mine", "d", "D");

        var seen = new List<string>();
        for (var page = 0; page < 5; page++) {
            var delta = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(token, limit: 1));
            delta.Status.Should().Be(207);
            var truncated = delta.Responses.TryGetValue(CollectionHref, out var marker);
            seen.AddRange(delta.Responses.Keys.Where(static href => href != CollectionHref));
            token = delta.SyncToken;
            if (!truncated) break;
            marker!.Child(DavNames.D.Status)!.Text.Should().Contain(" 507 ");
        }

        seen.Should().Equal(CollectionHref + "a.txt", CollectionHref + "b.txt", CollectionHref + "c.txt", CollectionHref + "d.txt");
    }

    /// <summary>
    /// Apple sync-report.xml "Prefer:return=minimal" 1, 2 (S-16): the minimal preference also drops the 404 propstat of
    /// an unknown property inside a sync-collection answer.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncCollection_HonoursReturnMinimal(bool minimal) {
        await using var harness = await WithMembersAsync("a");

        var response = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(null, props: "T:foobar"),
            headers: minimal ? Headers(("Prefer", "return=minimal")) : null);

        response.Status.Should().Be(207);
        response.IsMissing(CollectionHref + "a.txt", new DavName(TestThingsProtocol.Ns, "foobar")).Should().Be(!minimal);
        response.Prop(CollectionHref + "a.txt", DavNames.D.GetETag).Should().NotBeNull();
    }

    /// <summary>
    /// Apple sync-report.xml "diff token - props" (S-12) and Radicale _report_sync_token (S10): a member property that
    /// needs the content is answered in a delta as well as in the initial sync.
    /// </summary>
    [Fact]
    public async Task SyncCollection_ADeltaAnswersContentPropertiesOfChangedMembers() {
        await using var harness = await WithMembersAsync("a");
        var token = (await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(null))).SyncToken;
        await harness.PublishAsync("shifts", "mine", "a", "A2");

        var delta = await harness.SendAsync("REPORT", Collection, DavTestHarness.SyncCollection(token, props: "T:data"));

        delta.Prop(CollectionHref + "a.txt", Data)!.Text.Should().Be("A2");
    }

    /// <summary>A stream that counts what was read.</summary>
    private sealed class CountingStream(long length) : Stream {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            var n = (int)Math.Min(count, length - BytesRead);
            Array.Fill(buffer, (byte)'x', offset, n);
            BytesRead += n;
            return n;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
