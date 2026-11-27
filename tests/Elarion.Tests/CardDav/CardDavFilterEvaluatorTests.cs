using System.Text;
using AwesomeAssertions;
using Elarion.CardDav;
using Elarion.Contacts;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>
/// The filter evaluator over cards as producers write them (the vCard research note): Apple's grouped labels, Google's
/// escaped colons, Android's quoted-printable 2.1 read back, an organization card (FR-022, FR-023).
/// </summary>
public sealed class CardDavFilterEvaluatorTests {
    private const string Apple =
        "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Jörg Müller\r\nN:Müller;Jörg;;;\r\nitem1.EMAIL;type=INTERNET;type=pref:joerg@example.com\r\n"
        + "item1.X-ABLabel:_$!<Other>!$_\r\nTEL;type=CELL;type=VOICE:+43 660 1234567\r\nUID:apple-1\r\nEND:VCARD\r\n";

    private const string Google =
        "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Ada Lovelace\r\nN:Lovelace;Ada;;;\r\nURL:http\\://example.com/ada\r\nNOTE:Line one\\nLine two\\, more\r\n"
        + "UID:google-1\r\nEND:VCARD\r\n";

    private const string Android =
        "BEGIN:VCARD\r\nVERSION:2.1\r\nN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:=C3=96ztoprak;Ay=C5=9Fe;;;\r\n"
        + "FN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:Ay=C5=9Fe =C3=96ztoprak\r\nTEL;CELL:+90 555 000\r\nEND:VCARD\r\n";

    private const string Organization =
        "BEGIN:VCARD\r\nVERSION:4.0\r\nKIND:org\r\nFN:Example GmbH\r\nORG:Example GmbH\r\nUID:org-1\r\nEND:VCARD\r\n";

    public static TheoryData<string, string, bool> Cases => new() {
        { Apple, "<CARD:prop-filter name=\"EMAIL\"><CARD:text-match>JOERG@</CARD:text-match></CARD:prop-filter>", true },
        { Apple, "<CARD:prop-filter name=\"X-ABLABEL\"><CARD:text-match>Other</CARD:text-match></CARD:prop-filter>", true },
        { Apple, "<CARD:prop-filter name=\"TEL\"><CARD:param-filter name=\"TYPE\"><CARD:text-match match-type=\"equals\">cell</CARD:text-match></CARD:param-filter></CARD:prop-filter>", true },
        { Apple, "<CARD:prop-filter name=\"TEL\"><CARD:param-filter name=\"TYPE\"><CARD:text-match match-type=\"equals\">fax</CARD:text-match></CARD:param-filter></CARD:prop-filter>", false },
        { Apple, "<CARD:prop-filter name=\"FN\"><CARD:text-match>MÜLLER</CARD:text-match></CARD:prop-filter>", true },
        { Apple, "<CARD:prop-filter name=\"FN\"><CARD:text-match collation=\"i;ascii-casemap\">MÜLLER</CARD:text-match></CARD:prop-filter>", false },
        { Apple, "<CARD:prop-filter name=\"FN\"><CARD:text-match collation=\"i;octet\">jörg</CARD:text-match></CARD:prop-filter>", false },
        { Apple, "<CARD:prop-filter name=\"NOTE\"><CARD:is-not-defined/></CARD:prop-filter>", true },
        { Apple, "<CARD:prop-filter name=\"EMAIL\"><CARD:param-filter name=\"X-NONE\"><CARD:is-not-defined/></CARD:param-filter></CARD:prop-filter>", true },
        { Google, "<CARD:prop-filter name=\"URL\"><CARD:text-match match-type=\"starts-with\">http://</CARD:text-match></CARD:prop-filter>", true },
        { Google, "<CARD:prop-filter name=\"NOTE\"><CARD:text-match>two, more</CARD:text-match></CARD:prop-filter>", true },
        { Google, "<CARD:prop-filter name=\"FN\"><CARD:text-match negate-condition=\"yes\">Ada</CARD:text-match></CARD:prop-filter>", false },
        { Android, "<CARD:prop-filter name=\"FN\"><CARD:text-match>ayşe</CARD:text-match></CARD:prop-filter>", true },
        { Android, "<CARD:prop-filter name=\"N\"><CARD:text-match match-type=\"starts-with\">Öztoprak;</CARD:text-match></CARD:prop-filter>", true },
        { Organization, "<CARD:prop-filter name=\"KIND\"><CARD:text-match match-type=\"equals\">org</CARD:text-match></CARD:prop-filter>", true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AFilterMatchesTheCardAsTheRfcSays(string card, string propFilters, bool expected) {
        var properties = await PropertiesAsync(card);
        var filter = Filter(propFilters);

        CardDavFilterEvaluator.Validate(filter).Should().BeNull();
        CardDavFilterEvaluator.Matches(properties, filter).Should().Be(expected);
    }

    [Fact]
    public async Task TheFiltersTestCombinesItsPropFilters() {
        var properties = await PropertiesAsync(Apple);
        const string both = "<CARD:prop-filter name=\"FN\"><CARD:text-match>Jörg</CARD:text-match></CARD:prop-filter>"
                            + "<CARD:prop-filter name=\"NOTE\"><CARD:text-match>x</CARD:text-match></CARD:prop-filter>";

        CardDavFilterEvaluator.Matches(properties, Filter(both)).Should().BeTrue();
        CardDavFilterEvaluator.Matches(properties, Filter(both, " test=\"allof\"")).Should().BeFalse();
        CardDavFilterEvaluator.Matches(properties, Filter("")).Should().BeTrue();
    }

    private static DavXmlElement Filter(string propFilters, string test = "") =>
        DavXml.Parse(Encoding.UTF8.GetBytes($"<CARD:filter xmlns:CARD=\"urn:ietf:params:xml:ns:carddav\"{test}>{propFilters}</CARD:filter>"),
            new DavXmlLimits(1 << 20, 32, 10_000)).Root!;

    private static async Task<IReadOnlyList<ContactProperty>> PropertiesAsync(string card) {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(card));
        var read = await new ContactReader().ReadAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        return read.Entries.Single().Properties;
    }
}
