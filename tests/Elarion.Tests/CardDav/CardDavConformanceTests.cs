using System.Text;
using AwesomeAssertions;
using Elarion.CardDav;
using Elarion.Contacts;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>
/// CardDAV cases ported from other servers' and clients' suites (Apple ccs-caldavtester, sabre/dav and sabre/vobject,
/// Radicale, Xandikos, Cyrus, DAVx5, vdirsyncer, go-webdav): addressbook-query filters, multiget hrefs, address-data and
/// vCard 3.0/4.0 conversion. Each test names the cases it ports; where a suite disagrees with a documented Elarion
/// choice, the test pins the choice.
/// </summary>
public sealed class CardDavConformanceTests {
    private const string Book = "/addressbooks/alice/customers.all/";
    private const string BookHref = "/dav/addressbooks/alice/customers.all/";
    private const string Ns = CardDavTestHarness.Namespaces;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IEnumerable<string> Members(DavTestResponse answer) =>
        answer.Responses.Where(static r => r.Value.Child(DavNames.D.Status) is null).Select(static r => r.Key);

    private static async Task<CardDavTestHarness> WithContactsAsync() {
        var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger", "anna@example.com"));
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-2", "Bernd", "Huber", "bernd@example.org"));
        return harness;
    }

    private static async Task<ContactEntry> ReadEntryAsync(string card) {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(card));
        var read = await new ContactReader().ReadAsync(stream, cancellationToken: Ct);
        return read.Entries.Single();
    }

    private static async Task<Contact> ReadContactAsync(string card) => (await ReadEntryAsync(card)).Value!;

    private static IEnumerable<string> ValuesOf(ContactProperty property, string parameter) =>
        property.Parameters.Where(p => string.Equals(p.Name, parameter, StringComparison.OrdinalIgnoreCase)).SelectMany(static p => p.Values);

    private static string Card(params string[] lines) => "BEGIN:VCARD\r\n" + string.Concat(lines.Select(static l => l + "\r\n")) + "END:VCARD\r\n";

    // ---- addressbook-query: the empty filter and what every result carries ----

    /// <summary>
    /// DAVx5 "addressbookQuery request body contains query and filter" (QY-29) sends an empty self-closed
    /// <c>&lt;CARD:filter /&gt;</c> with getetag only; Apple CardDAV reports.xml "basic query" 1 (C-01), Radicale
    /// test_addressbook_empty_filter and Cyrus empty_filter (C01): every card matches, each with its ETag, and no
    /// address-data is answered when none was asked for.
    /// </summary>
    [Fact]
    public async Task AnEmptyFilterElement_AsDavx5SendsIt_MatchesEveryCardWithItsETag() {
        await using var harness = await WithContactsAsync();

        var answer = await harness.SendAsync("REPORT", Book,
            $"<CARD:addressbook-query {Ns}><D:prop><D:getetag /></D:prop><CARD:filter /></CARD:addressbook-query>",
            headers: new Dictionary<string, string> { ["Depth"] = "1" });

        answer.Status.Should().Be(207);
        Members(answer).Should().BeEquivalentTo(BookHref + "c-1.vcf", BookHref + "c-2.vcf");
        foreach (var href in Members(answer)) {
            answer.Prop(href, DavNames.D.GetETag)!.Text.Should().StartWith("\"");
            answer.Prop(href, DavNames.Card.AddressData).Should().BeNull();
        }
    }

    /// <summary>
    /// vdirsyncer get_multi and _parse_prop_responses (MG-10, SY-26), python-caldav sync fallback (SY-25), Radicale
    /// test_addressbook_getcontenttype (D18), sabre SogoStripContentTypeTest testDontStrip (C29): every member a query, a
    /// multiget, a Depth 1 PROPFIND or a sync answers carries its ETag (clients drop data without one), and the listing
    /// carries the vCard content type.
    /// </summary>
    [Fact]
    public async Task EveryMemberResult_CarriesItsETag_AndTheListingItsContentType() {
        await using var harness = await WithContactsAsync();

        var query = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Query(null));
        var multiget = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Multiget("<CARD:address-data/>", BookHref + "c-1.vcf", BookHref + "c-2.vcf"));
        var listing = await harness.SendAsync("PROPFIND", Book, CardDavTestHarness.PropFind("D:getetag", "D:getcontenttype"),
            headers: new Dictionary<string, string> { ["Depth"] = "1" });
        var sync = await harness.SendAsync("REPORT", Book, CardDavTestHarness.SyncCollection(null, "<CARD:address-data/>"));

        foreach (var answer in new[] { query, multiget, listing, sync }) {
            var etags = new[] { "c-1.vcf", "c-2.vcf" }.Select(name => answer.Prop(BookHref + name, DavNames.D.GetETag)?.Text).ToList();
            etags.Should().OnlyContain(static e => e != null && e.StartsWith('"'));
        }

        listing.Prop(BookHref + "c-1.vcf", DavNames.D.GetContentType)!.Text.Should().Be("text/vcard; charset=utf-8");
        multiget.Prop(BookHref + "c-1.vcf", DavNames.D.GetETag)!.Text.Should().Be(query.Prop(BookHref + "c-1.vcf", DavNames.D.GetETag)!.Text);
    }

    // ---- the filter evaluator, over cards as other suites store them ----

    private const string Gump = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:gump\r\nFN:Forrest Gump\r\nN:Gump;Forrest;;Mr.;\r\n"
                                + "ORG:Example Corporation;Engineering Department\r\nNICKNAME:foo,eric\r\nNOTE:\r\nPHOTO;BASE64:\r\n"
                                + "item1.ADR;type=WORK:;;1 Infinite Loop;Exampletino\\, CA 99999;;;Japan\r\nitem1.X-ABADR:jp\r\n"
                                + "item2.URL:http://www.example.jp/~magic\r\nitem3.X-JABBER:gump@jabber.example\r\n"
                                + "item4.EMAIL;type=INTERNET:a@example.com\r\nitem5.EMAIL;type=INTERNET:b@111.com\r\nitem6.EMAIL;type=INTERNET:c@example.org\r\n"
                                + "TEL;TYPE=WORK:+1 111\r\nTEL;TYPE=IPHONE;TYPE=pref:+1 222\r\nitem7.TEL:+1 444\r\nEND:VCARD\r\n";

    private const string Unicode = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:unicode\r\nFN:Źacc Ňug\r\nNICKNAME:ダブル バイト\r\nNOTE:FÖÖBAR\r\n"
                                   + "TITLE:Café Example\r\nEND:VCARD\r\n";

    private const string IPhoneOnly = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:iphone\r\nFN:Only Phone\r\nTEL;TYPE=IPHONE;TYPE=pref:+1 222\r\nEND:VCARD\r\n";

    private static string PropFilter(string name, string body = "", string test = "") => $"<CARD:prop-filter name=\"{name}\"{test}>{body}</CARD:prop-filter>";

    private static string TextMatch(string text, string attributes = "") => $"<CARD:text-match{attributes}>{text}</CARD:text-match>";

    private const string Negate = " negate-condition=\"yes\"";
    private const string IsNotDefined = "<CARD:is-not-defined/>";

    /// <summary>The filter cases; each row names its source.</summary>
    public static TheoryData<string, string, string, bool> FilterCases => new() {
        // Cyrus addressbook_query_negate (C16), Radicale test_text_match_filter (Q08), python-caldav pending-task filters
        // (QY-09): a negated text-match on a property the card does not have does not match; on one it has, it negates.
        { "Cyrus C16: negate on an absent property", Gump, PropFilter("TITLE", TextMatch("Cyrus", Negate)), false },
        { "Cyrus C16: negate on a present property", Gump, PropFilter("FN", TextMatch("Cyrus", Negate)), true },
        // Cyrus filter (C10), Xandikos test_filter_org_structured_property/test_filter_n_property (C10), Radicale
        // test_addressbook_prop_filter_structured (C07): structured values match as a whole, separators included.
        { "Cyrus C10: N contains 'gump;'", Gump, PropFilter("N", TextMatch("gump;")), true },
        { "Xandikos C10: N contains a component", Gump, PropFilter("N", TextMatch("Forrest")), true },
        { "Xandikos C10: ORG contains its unit", Gump, PropFilter("ORG", TextMatch("Engineering")), true },
        { "Radicale C07: N without the needle", Gump, PropFilter("N", TextMatch("nonexistent")), false },
        // Apple reports.xml 28 (C-20): an escaped comma in a compound value is compared unescaped.
        { "Apple C-20: ADR with an escaped comma", Gump, PropFilter("ADR", TextMatch("Exampletino, CA")), true },
        // Apple reports.xml 28, 29 (C-19), sabre ValidateFilterTest (C19): grouped properties match by name without the group.
        { "Apple C-19: grouped ADR", Gump, PropFilter("ADR", TextMatch("Japan")), true },
        { "Apple C-19: grouped URL", Gump, PropFilter("URL", TextMatch("http://www.example.jp", " match-type=\"starts-with\"")), true },
        { "Apple C-19: grouped X-JABBER exists", Gump, PropFilter("X-JABBER"), true },
        // sabre ValidateFilterTest row 20 (C24): negation is per instance; one grouped EMAIL without @111.com suffices.
        { "sabre C24: negate per grouped instance", Gump, PropFilter("email", TextMatch("@111.com", Negate)), true },
        // Apple reports.xml 2, 6, 7 (C-02, C-03), Xandikos test_filter_with_empty_property (C21): an empty value is defined.
        { "Apple C-03: empty PHOTO is defined", Gump, PropFilter("PHOTO"), true },
        { "Apple C-02: empty PHOTO is not undefined", Gump, PropFilter("PHOTO", IsNotDefined), false },
        { "Xandikos C21: empty NOTE is defined", Gump, PropFilter("NOTE"), true },
        // Apple nonascii.xml 4 (C-23), Radicale test_addressbook_prop_filter_empty_text_match (C02): an empty needle
        // matches every present value, negated none.
        { "Apple C-23: empty needle", Gump, PropFilter("FN", TextMatch("", " collation=\"i;unicode-casemap\" match-type=\"contains\"")), true },
        { "Radicale C02: negated empty needle", Gump, PropFilter("FN", TextMatch("", Negate)), false },
        // sabre ValidateFilterTest rows 10–13 (C21) evaluate a param-filter across all instances, so "TYPE is-not-defined"
        // is false when any TEL has a TYPE; RFC 6352 §10.5.1 tests each property instance, and item7.TEL has none. Elarion
        // follows the RFC: pinned.
        { "sabre C21: TYPE is-not-defined holds for the TEL without TYPE (RFC, not sabre)", Gump,
            PropFilter("TEL", "<CARD:param-filter name=\"TYPE\">" + IsNotDefined + "</CARD:param-filter>"), true },
        { "sabre C21: TYPE defined", Gump, PropFilter("TEL", "<CARD:param-filter name=\"type\"/>"), true },
        { "sabre C21: FOO defined", Gump, PropFilter("TEL", "<CARD:param-filter name=\"FOO\"/>"), false },
        { "sabre C21: FOO is-not-defined", Gump, PropFilter("TEL", "<CARD:param-filter name=\"FOO\">" + IsNotDefined + "</CARD:param-filter>"), true },
        // sabre ValidateFilterTest rows 21–23 (C25): a negated parameter text-match holds when the value lacks the needle.
        { "sabre C25: TYPE contains WORK, absent", IPhoneOnly,
            PropFilter("TEL", "<CARD:param-filter name=\"TYPE\">" + TextMatch("WORK") + "</CARD:param-filter>"), false },
        { "sabre C25: TYPE not contains WORK", IPhoneOnly,
            PropFilter("TEL", "<CARD:param-filter name=\"TYPE\">" + TextMatch("WORK", Negate) + "</CARD:param-filter>"), true },
        // Xandikos test_ascii_casemap_non_ascii (C20), sabre StringUtilTest rows 15–42 (Q16, Q17): i;ascii-casemap folds
        // only A–Z; i;unicode-casemap (the CardDAV default) folds beyond ASCII.
        { "Xandikos C20: ascii-casemap equals with ASCII case", Unicode,
            PropFilter("TITLE", TextMatch("café example", " collation=\"i;ascii-casemap\" match-type=\"equals\"")), true },
        { "Xandikos C20: ascii-casemap leaves É alone", Unicode,
            PropFilter("TITLE", TextMatch("CAFÉ EXAMPLE", " collation=\"i;ascii-casemap\" match-type=\"equals\"")), false },
        { "sabre Q17: unicode-casemap folds Ö", Unicode, PropFilter("NOTE", TextMatch("föö")), true },
        { "sabre Q16: ascii-casemap does not fold Ö", Unicode, PropFilter("NOTE", TextMatch("föö", " collation=\"i;ascii-casemap\"")), false },
        // Apple reports.xml 9, 10 (C-07, C-08), Xandikos test_filter_with_unicode_in_fn (C21): non-ASCII needles.
        { "Apple C-07: Latin Extended-A, other case", Unicode, PropFilter("FN", TextMatch("ň")), true },
        { "Apple C-08: Katakana", Unicode, PropFilter("NICKNAME", TextMatch("ダ")), true },
        // sabre AddressBookQueryTest testQuery (C01): property and parameter names in filters are case-insensitive.
        { "sabre C01: lower-case property name", Gump, PropFilter("uid"), true },
        // Cyrus filter (C09): NICKNAME is a text list (RFC 6350 §6.2.3), so equals compares each comma-separated value.
        { "Cyrus C09: equals one value of a text list", Gump, PropFilter("NICKNAME", TextMatch("eric", " match-type=\"equals\"")), true },
    };

    [Theory]
    [MemberData(nameof(FilterCases))]
    public async Task AFilterMatchesAsTheSourceSuiteExpects(string source, string card, string propFilters, bool expected) {
        var properties = (await ReadEntryAsync(card)).Properties;
        var filter = DavXml.Parse(Encoding.UTF8.GetBytes($"<CARD:filter xmlns:CARD=\"urn:ietf:params:xml:ns:carddav\">{propFilters}</CARD:filter>"),
            new DavXmlLimits(1 << 20, 32, 10_000)).Root!;

        CardDavFilterEvaluator.Validate(filter).Should().BeNull(source);
        CardDavFilterEvaluator.Matches(properties, filter).Should().Be(expected, source);
    }

    /// <summary>
    /// sabre AddressBookQueryReportTest testDeserializeBadTest (C15) and go-webdav invalid-query-filter (QY-27) answer
    /// 400 for an unknown <c>test</c> value; Elarion answers it like an unknown match-type, 403 supported-filter. Pinned.
    /// </summary>
    [Theory]
    [InlineData("<CARD:prop-filter name=\"FN\"/>", " test=\"bad\"")]
    [InlineData("<CARD:prop-filter name=\"FN\" test=\"XXX-invalid\"><CARD:text-match>a</CARD:text-match></CARD:prop-filter>", "")]
    public async Task AnUnknownTestValue_Answers403SupportedFilter(string filter, string test) {
        await using var harness = await WithContactsAsync();

        var answer = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Query(filter, test: test));

        answer.Status.Should().Be(403);
        answer.Xml.Child(DavNames.Card.SupportedFilter).Should().NotBeNull();
    }

    /// <summary>Apple reports.xml 10 (C-08) end to end: a Katakana needle in the XML body finds the card whose FN holds it.</summary>
    [Fact]
    public async Task ANonAsciiNeedle_FindsTheCardEndToEnd() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", new Contact { Uid = "jp", FormattedName = "ダブル バイト" });
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("other", "Anna", "Berger"));

        var answer = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Query(PropFilter("FN", TextMatch("ダ"))));

        Members(answer).Should().Equal(BookHref + "jp.vcf");
        answer.Prop(BookHref + "jp.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("FN:ダブル バイト");
    }

    /// <summary>
    /// Apple CardDAV reports.xml 17, 18 (C-12), sabre AddressBookQueryTest testQueryDepth0 (C02), Radicale
    /// test_report_item (Q21): an addressbook-query sent to a card's URL (RFC 6352 §8.6: its scope is that resource)
    /// answers 207 with the card when it matches and an empty 207 when it does not.
    /// </summary>
    [Fact]
    public async Task AQueryOnACardsUrl_AnswersThatCardOrNothing() {
        await using var harness = await WithContactsAsync();

        var match = await harness.SendAsync("REPORT", Book + "c-1.vcf", CardDavTestHarness.Query(PropFilter("FN", TextMatch("Anna"))),
            headers: new Dictionary<string, string> { ["Depth"] = "0" });
        var noMatch = await harness.SendAsync("REPORT", Book + "c-1.vcf", CardDavTestHarness.Query(PropFilter("FN", TextMatch("Bernd"))),
            headers: new Dictionary<string, string> { ["Depth"] = "0" });

        match.Status.Should().Be(207);
        Members(match).Should().Equal(BookHref + "c-1.vcf");
        noMatch.Status.Should().Be(207);
        noMatch.Responses.Should().BeEmpty();
    }

    // ---- multiget hrefs and address-data ----

    /// <summary>
    /// DAVx5 "multiget request body keeps percent-encoded hrefs" (MG-07), caldav-server-tester "robur takes a %40 href"
    /// and python-caldav TestAnHrefInAReportBody (MG-06), dav4jvm "member with differently encoded href" (RP-25): an href
    /// naming <c>@</c> as <c>%40</c>, literally, or with another escaped character reaches the card. The response names
    /// the card by its canonical href (<c>%40</c>), which those clients match after decoding; Elarion's choice, pinned.
    /// </summary>
    [Theory]
    [InlineData("anna%40example.com.vcf")]
    [InlineData("anna@example.com.vcf")]
    [InlineData("anna%40example%2Ecom.vcf")]
    public async Task MultigetHrefsWithPercent40OrALiteralAt_ReachTheCard(string name) {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("anna@example.com", "Anna", "Berger"));

        var answer = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Multiget("<CARD:address-data/>", BookHref + name));

        answer.Status.Should().Be(207);
        Members(answer).Should().Equal(BookHref + "anna%40example.com.vcf");
        answer.Prop(BookHref + "anna%40example.com.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("UID:anna@example.com");
    }

    /// <summary>
    /// Cyrus multiget (M01) answers 403 for ancestors and garbage and 404 for missing cards; Radicale
    /// test_get_vcard_exceed_size (M02) expands a collection href into its members. Elarion answers every href that
    /// names no card of the address book (garbage, an ancestor, the address book itself, a card of another book) with
    /// its own 404 and never expands a collection. Pinned.
    /// </summary>
    [Fact]
    public async Task MultigetHrefsThatNameNoCard_EachAnswer404_AndACollectionIsNotExpanded() {
        await using var harness = await WithContactsAsync();
        string[] hrefs = ["nonsense", "/dav/addressbooks/", "/dav/addressbooks/alice/", BookHref, BookHref.TrimEnd('/'),
            "/dav/addressbooks/alice/teams.red/c-1.vcf", BookHref + "c-1.vcf"];

        var answer = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Multiget("<CARD:address-data/>", hrefs));

        answer.Status.Should().Be(207);
        answer.Responses.Keys.Should().Equal(hrefs);
        Members(answer).Should().Equal(BookHref + "c-1.vcf");
        foreach (var href in hrefs[..^1]) answer.Responses[href].Child(DavNames.D.Status)!.Text.Should().Contain(" 404 ");
    }

    /// <summary>
    /// vdirsyncer CardDAVStorage get_multi_template (MG-09) sends <c>Content-Type="text/vcard" version="4.0"</c> with a
    /// capitalised attribute name; DAVx5 "multiget with contentType" (MG-08) the lower-case one. Both are answered as
    /// vCard 4.0, never refused.
    /// </summary>
    [Theory]
    [InlineData("<CARD:address-data Content-Type=\"text/vcard\" version=\"4.0\"/>")]
    [InlineData("<CARD:address-data content-type=\"text/vcard\" version=\"4.0\"/>")]
    public async Task AddressDataAsVdirsyncerAndDavx5SendIt_IsAnsweredAsVCard40(string addressData) {
        await using var harness = await WithContactsAsync();

        var answer = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Multiget(addressData, BookHref + "c-1.vcf"));

        answer.Status.Should().Be(207);
        answer.Prop(BookHref + "c-1.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("VERSION:4.0");
    }

    /// <summary>
    /// sabre/vobject VCardConverterTest testConvert30to30 (V03), sabre MultiGetTest testMultiGet (C10): asking for the
    /// stored version passes the stored bytes through unchanged, byte for byte equal to GET.
    /// </summary>
    [Fact]
    public async Task AddressDataOfTheStoredVersion_IsTheStoredBytes() {
        await using var harness = await WithContactsAsync();

        var get = await harness.SendAsync("GET", Book + "c-1.vcf");
        var answer = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Multiget("<CARD:address-data content-type=\"text/vcard\" version=\"3.0\"/>", BookHref + "c-1.vcf"));

        answer.Prop(BookHref + "c-1.vcf", DavNames.Card.AddressData)!.Text.Should().Be(get.Body);
    }

    /// <summary>
    /// sabre CardDAV PluginTest testGetTransform (C27), sabre negotiateVCard (C28), sabre ICSExportPluginTest
    /// testNegotiateDefault (M09, the same rule for cards): an Accept naming <c>text/x-vcard</c>, jCard or an unrelated
    /// type answers the stored vCard 3.0, never 406.
    /// </summary>
    [Theory]
    [InlineData("text/x-vcard")]
    [InlineData("application/vcard+json")]
    [InlineData("text/plain")]
    public async Task GetWithAnAcceptElarionDoesNotServe_AnswersTheStoredVCard30(string accept) {
        await using var harness = await WithContactsAsync();

        var answer = await harness.SendAsync("GET", Book + "c-1.vcf", headers: new Dictionary<string, string> { ["Accept"] = accept });

        answer.Status.Should().Be(200);
        answer.ContentType.Should().Be("text/vcard; charset=utf-8");
        answer.Body.Should().Contain("VERSION:3.0");
    }

    /// <summary>
    /// Cyrus get_control_char and control_chars_repaired (H04), vdirsyncer test_xml_specialchars (RP-29): a card whose
    /// value holds a C0 control character (illegal in XML 1.0) is served inside a multistatus that stays well-formed,
    /// with the character dropped.
    /// </summary>
    [Fact]
    public async Task AControlCharacterInACard_KeepsTheMultistatusWellFormed() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", new Contact { Uid = "ctrl", FormattedName = "Forrest\bGump", Note = "ct\u0015rl" });

        var answer = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Multiget("<CARD:address-data/>", BookHref + "ctrl.vcf"));

        answer.Status.Should().Be(207);
        var card = answer.Prop(BookHref + "ctrl.vcf", DavNames.Card.AddressData)!.Text;
        card.Should().NotContain("\b").And.NotContain("\u0015").And.Contain("Forrest");
    }

    // ---- vCard 3.0 and 4.0 conversion (sabre/vobject VCardConverterTest) ----

    /// <summary>
    /// sabre/vobject VCardConverterTest testConvert30to40 (V01) and testPhoneNumberValueTypeGetsRemoved (V10), as they
    /// apply to a card a client reads as vCard 4.0: CHARSET is dropped, a <c>pref</c> TYPE becomes <c>PREF=1</c>, groups
    /// and their labels stay, an inline photo becomes a data: URI, Apple's <c>X-ABShowAs:COMPANY</c> becomes
    /// <c>KIND:org</c>, and <c>VALUE=PHONE-NUMBER</c> (not a 4.0 value type) is dropped.
    /// </summary>
    [Fact]
    public async Task AVCard30Card_ReadAsVCard40_ConvertsAsSabreDoes() {
        await using var harness = CardDavTestHarness.Create();
        var png = Convert.ToBase64String(CardDavTestData.Png);
        await harness.PublishAsync("customers", "all", await ReadContactAsync(Card("VERSION:3.0", "UID:conv-1", "FN;CHARSET=UTF-8:Steve",
            "TEL;TYPE=PREF,HOME:+1 555 666 777", "TEL;TYPE=HOME;VALUE=PHONE-NUMBER:+1234", "item1.TEL:+1 444 555 666", "item1.X-ABLABEL:CustomLabel",
            "PHOTO;ENCODING=b;TYPE=PNG:" + png, "X-ABShowAs:COMPANY")));

        var answer = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Multiget("<CARD:address-data content-type=\"text/vcard\" version=\"4.0\"/>", BookHref + "conv-1.vcf"));
        var text = answer.Prop(BookHref + "conv-1.vcf", DavNames.Card.AddressData)!.Text;
        var card = (await ReadEntryAsync(text)).Properties;

        text.Should().Contain("VERSION:4.0").And.NotContain("CHARSET").And.NotContain("PHONE-NUMBER");
        card.Single(static p => p.Name == "FN").Value.Should().Be("Steve");
        var preferred = card.Single(static p => p.Name == "TEL" && p.Value == "+1 555 666 777");
        ValuesOf(preferred, "PREF").Should().Equal("1");
        ValuesOf(preferred, "TYPE").Select(static t => t.ToUpperInvariant()).Should().Equal("HOME");
        card.Single(static p => p.Name == "TEL" && p.Value == "+1234").Parameters.Should().NotContain(static p => p.Name == "VALUE");
        var grouped = card.Single(static p => p.Name == "TEL" && p.Value == "+1 444 555 666");
        grouped.Group.Should().NotBeNull();
        card.Should().Contain(p => p.Group != null && string.Equals(p.Group, grouped.Group, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(p.Name, "X-ABLABEL", StringComparison.OrdinalIgnoreCase) && p.Value == "CustomLabel");
        card.Single(static p => p.Name == "PHOTO").Value.Should().Be("data:image/png;base64," + png);
        card.Single(static p => p.Name == "KIND").Value.Should().BeEquivalentTo("org");
    }

    /// <summary>
    /// sabre/vobject VCardConverterTest testConvert40to30 (V02) and testConvertIndividualCard (V07), as they apply to a
    /// 4.0 card the application read and published (stored as 3.0): <c>PREF=1</c> becomes a <c>pref</c> TYPE, a data:
    /// URI photo becomes <c>ENCODING=b;TYPE=PNG</c>, <c>KIND:org</c> becomes <c>X-ABShowAs:COMPANY</c>, and the default
    /// <c>KIND:individual</c> is left out of 3.0 and not added back in 4.0.
    /// </summary>
    [Fact]
    public async Task AVCard40Card_IsStoredAsVCard30_AsSabreConvertsIt() {
        await using var harness = CardDavTestHarness.Create();
        var png = Convert.ToBase64String(CardDavTestData.Png);
        await harness.PublishAsync("customers", "all", await ReadContactAsync(Card("VERSION:4.0", "UID:org-1", "FN:Example Org",
            "TEL;PREF=1;TYPE=HOME:+1 555", "PHOTO:data:image/png;base64," + png, "KIND:org")));
        await harness.PublishAsync("customers", "all", await ReadContactAsync(Card("VERSION:4.0", "UID:person-1", "FN:Jane Doe", "KIND:individual")));

        var org = (await ReadEntryAsync((await harness.SendAsync("GET", Book + "org-1.vcf")).Body)).Properties;
        var person = (await harness.SendAsync("GET", Book + "person-1.vcf")).Body;
        var person40 = (await harness.SendAsync("REPORT", Book,
                CardDavTestHarness.Multiget("<CARD:address-data content-type=\"text/vcard\" version=\"4.0\"/>", BookHref + "person-1.vcf")))
            .Prop(BookHref + "person-1.vcf", DavNames.Card.AddressData)!.Text;

        var tel = org.Single(static p => p.Name == "TEL");
        ValuesOf(tel, "TYPE").Select(static t => t.ToUpperInvariant()).Should().BeEquivalentTo("HOME", "PREF");
        ValuesOf(tel, "PREF").Should().BeEmpty();
        var photo = org.Single(static p => p.Name == "PHOTO");
        ValuesOf(photo, "ENCODING").Should().Equal("b");
        ValuesOf(photo, "TYPE").Select(static t => t.ToUpperInvariant()).Should().Equal("PNG");
        photo.Value.Should().Be(png);
        org.Single(static p => string.Equals(p.Name, "X-ABSHOWAS", StringComparison.OrdinalIgnoreCase)).Value.Should().Be("COMPANY");
        org.Should().NotContain(static p => p.Name == "KIND");
        person.Should().NotContain("KIND").And.NotContain("X-ABSHOWAS");
        person40.Should().NotContain("KIND");
    }

    /// <summary>
    /// sabre/vobject VCardConverterTest testConvertGroupCard (V05) and Cyrus convert Apple's group card
    /// (<c>X-ADDRESSBOOKSERVER-KIND:group</c>, <c>X-ADDRESSBOOKSERVER-MEMBER</c>) to <c>KIND:group</c> and <c>MEMBER</c> in
    /// vCard 4.0. Elarion's choice, pinned: contact groups are out of scope (ADR-0113 section 4), so the 4.0 card keeps
    /// Apple's properties as the 3.0 card had them, losslessly, and no group is invented.
    /// </summary>
    [Fact]
    public async Task AnAppleGroupCard_ReadAsVCard40_KeepsApplesPropertiesAsTheyAre() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", await ReadContactAsync(Card("VERSION:3.0", "UID:group-1", "FN:Team",
            "X-ADDRESSBOOKSERVER-KIND:group", "X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:a", "X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:b")));

        var answer = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Multiget("<CARD:address-data content-type=\"text/vcard\" version=\"4.0\"/>", BookHref + "group-1.vcf"));
        var text = answer.Prop(BookHref + "group-1.vcf", DavNames.Card.AddressData)!.Text;
        var card = (await ReadEntryAsync(text)).Properties;

        card.Should().NotContain(static p => p.Name == "KIND" || p.Name == "MEMBER", text);
        card.Where(static p => p.Name == "X-ADDRESSBOOKSERVER-KIND").Select(static p => p.Value).Should().Equal("group");
        card.Where(static p => p.Name == "X-ADDRESSBOOKSERVER-MEMBER").Select(static p => p.Value).Should().Equal("urn:uuid:a", "urn:uuid:b");
    }
}
