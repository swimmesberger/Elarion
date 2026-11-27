using System.Text;
using AwesomeAssertions;
using Elarion.ContentLines;
using Xunit;

namespace Elarion.Tests.ContentLines;

public sealed class ContentLineReaderTests {
    public static TheoryData<string> RuleSets() => ["vcard", "icalendar"];

    [Theory]
    [MemberData(nameof(RuleSets))]
    public void LineEnds_CrLfLfCrAndCrCrLfEachEndOneLine(string ruleSet) {
        var lines = ReadAll("A:1\r\nB:2\nC:3\rD:4\r\r\nE:5", Rules(ruleSet));

        lines.Select(static l => l.Text).Should().Equal("A:1", "B:2", "C:3", "D:4", "E:5");
        lines.Select(static l => l.Line).Should().Equal(1, 2, 3, 4, 5);
    }

    [Theory]
    [MemberData(nameof(RuleSets))]
    public void AFoldInsideAMultiByteCharacter_IsJoinedAsBytes(string ruleSet) {
        byte[] data = [.. "X:J"u8, 0xC3, (byte)'\r', (byte)'\n', (byte)' ', 0xA4, .. "ger"u8];

        var line = Assert.Single(ReadAll(data, Rules(ruleSet)));

        line.Text.Should().Be("X:Jäger");
    }

    [Fact]
    public void VCardUnfolding_RemovesEveryLeadingSpaceAndICalendarOnlyOne() {
        const string text = "X:a\r\n   b\r\n\tc";

        Assert.Single(ReadAll(text, Rules("vcard"))).Text.Should().Be("X:abc");
        Assert.Single(ReadAll(text, Rules("icalendar"))).Text.Should().Be("X:a  bc");
    }

    [Theory]
    [MemberData(nameof(RuleSets))]
    public void ALineOverTheLimit_IsConsumedWhole(string ruleSet) {
        var text = "X:" + new string('a', 100) + "\r\n " + new string('b', 100) + "\r\nY:1";

        var lines = ReadAll(text, Rules(ruleSet, maxLineBytes: 50));

        lines.Should().HaveCount(2);
        lines[0].TooLong.Should().BeTrue();
        lines[1].Text.Should().Be("Y:1");
    }

    [Fact]
    public void QuotedPrintableSoftBreaks_JoinOnlyUnderTheVCardRules() {
        const string text = "X;ENCODING=QUOTED-PRINTABLE:a=\r\nb=  \r\n c\r\nY:1";

        ReadAll(text, Rules("vcard")).Select(static l => l.Text).Should().Equal("X;ENCODING=QUOTED-PRINTABLE:abc", "Y:1");
        ReadAll(text, Rules("icalendar")).Select(static l => l.Text).Should().Equal("X;ENCODING=QUOTED-PRINTABLE:a=", "b=  c", "Y:1");
    }

    [Fact]
    public void AQuotedPrintableLineOverTheLimit_StillConsumesItsSoftBreaks() {
        var builder = new StringBuilder("X;ENCODING=QUOTED-PRINTABLE:");
        for (var i = 0; i < 100; i++) builder.Append("aaaaaaaaaa=\r\n");
        builder.Append("end\r\nY:1");

        var lines = ReadAll(builder.ToString(), Rules("vcard", maxLineBytes: 200));

        lines.Should().HaveCount(2);
        lines[0].TooLong.Should().BeTrue();
        lines[1].Text.Should().Be("Y:1");
        lines[1].Line.Should().Be(102);
    }

    [Fact]
    public void Base64_ContinuesToABlankLineOnlyUnderTheVCardRules() {
        const string text = "PHOTO;ENCODING=BASE64:AAAA\r\nBBBB\r\n\r\nY:1";

        ReadAll(text, Rules("vcard")).Select(static l => l.Text).Should().Equal("PHOTO;ENCODING=BASE64:AAAABBBB", "Y:1");
        ReadAll(text, Rules("icalendar")).Select(static l => l.Text).Should().Equal("PHOTO;ENCODING=BASE64:AAAA", "BBBB", "Y:1");
    }

    [Fact]
    public void CommentLines_AreSkippedOnlyUnderTheVCardRules() {
        ReadAll("# c\r\nY:1", Rules("vcard")).Select(static l => l.Text).Should().Equal("Y:1");
        ReadAll("# c\r\nY:1", Rules("icalendar")).Select(static l => l.Text).Should().Equal("# c", "Y:1");
    }

    [Fact]
    public void Parser_ReadsGroupsAndBareParametersOnlyUnderTheVCardRules() {
        var decoder = new ContentTextDecoder(Encoding.Latin1, fileTranscoded: false, useCharset: true);
        var line = "item1.tel;work;voice;TYPE=\"a,b\":1"u8;

        ContentLineParser.TryParse(line, Rules("vcard"), decoder, out var vcard).Should().BeTrue();
        vcard.Group.Should().Be("item1");
        vcard.Name.Should().Be("TEL");
        vcard.Parameters.Select(static p => $"{p.Name}={string.Join('|', p.Values)}").Should().Equal("TYPE=WORK", "TYPE=VOICE", "TYPE=a,b");

        ContentLineParser.TryParse(line, Rules("icalendar"), decoder, out var ical).Should().BeTrue();
        ical.Group.Should().BeNull();
        ical.Name.Should().Be("ITEM1.TEL");
        ical.Parameters[0].Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Writer_FoldsAt75OctetsOnCharacterBoundaries() {
        using var stream = new MemoryStream();
        using (var writer = new ContentLineWriter(stream)) {
            writer.WriteProperty(null, "NOTE", null, string.Concat(Enumerable.Repeat("äöü€", 40)));
            await writer.FlushAsync(TestContext.Current.CancellationToken);
        }

        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split("\r\n");
        foreach (var line in lines) Encoding.UTF8.GetByteCount(line).Should().BeLessThanOrEqualTo(75);
        lines.Should().NotContain(static l => l.Contains('�'));
        Assert.Single(ReadAll(stream.ToArray(), Rules("icalendar", maxLineBytes: 10_000))).Text
            .Should().Be("NOTE:" + string.Concat(Enumerable.Repeat("äöü€", 40)));
    }

    private static ContentLineRules Rules(string ruleSet, int maxLineBytes = 1024 * 1024) =>
        ruleSet == "vcard" ? ContentLineRules.VCard(maxLineBytes) : ContentLineRules.ICalendar(maxLineBytes);

    private static List<(string Text, int Line, bool TooLong)> ReadAll(string text, ContentLineRules rules) => ReadAll(Encoding.UTF8.GetBytes(text), rules);

    private static List<(string Text, int Line, bool TooLong)> ReadAll(byte[] data, ContentLineRules rules) {
        var lines = new List<(string, int, bool)>();
        using var reader = new ContentLineReader(data, rules);
        while (reader.Read()) lines.Add((Encoding.UTF8.GetString(reader.Current), reader.LineNumber, reader.IsTooLong));
        return lines;
    }
}
