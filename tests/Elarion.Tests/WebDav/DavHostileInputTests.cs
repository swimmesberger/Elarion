using System.Text;
using AwesomeAssertions;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.WebDav;

/// <summary>Request bodies are read with DTDs refused and bounded size, depth and element count (spec 0006 FR-025).</summary>
public sealed class DavHostileInputTests {
    [Fact]
    public async Task ADocumentTypeDeclaration_Answers400() {
        await using var harness = DavTestHarness.Create();

        var response = await harness.SendAsync("PROPFIND", "/",
            "<?xml version=\"1.0\"?><!DOCTYPE propfind [<!ENTITY x \"y\">]><D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>");

        response.Status.Should().Be(400);
    }

    [Fact]
    public async Task EntityExpansion_Answers400WithoutExpanding() {
        await using var harness = DavTestHarness.Create();
        var lol = "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">"
                  + "<!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">]>"
                  + "<D:propfind xmlns:D=\"DAV:\"><D:prop><D:displayname>&lol3;</D:displayname></D:prop></D:propfind>";

        (await harness.SendAsync("PROPFIND", "/", lol)).Status.Should().Be(400);
    }

    [Fact]
    public async Task AnExternalEntity_IsNeverResolved() {
        await using var harness = DavTestHarness.Create();
        var xxe = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY ext SYSTEM \"file:///etc/passwd\">]>"
                  + "<D:propfind xmlns:D=\"DAV:\"><D:prop><D:displayname>&ext;</D:displayname></D:prop></D:propfind>";

        var response = await harness.SendAsync("PROPFIND", "/", xxe);

        response.Status.Should().Be(400);
        response.Body.Should().NotContain("root:");
    }

    [Fact]
    public async Task ABodyOverTheLimit_Answers413WithoutReadingTheRest() {
        await using var harness = DavTestHarness.Create();
        var body = new CountingStream(100L * 1024 * 1024);

        var response = await harness.SendAsync("PROPFIND", "/", bodyStream: body, contentLength: null);

        response.Status.Should().Be(413);
        body.BytesRead.Should().BeLessThan(2L * 1024 * 1024);
    }

    [Fact]
    public async Task ADeclaredLengthOverTheLimit_Answers413WithoutReading() {
        await using var harness = DavTestHarness.Create();
        var body = new CountingStream(100L * 1024 * 1024);

        var response = await harness.SendAsync("PROPFIND", "/", bodyStream: body, contentLength: 100L * 1024 * 1024);

        response.Status.Should().Be(413);
        body.BytesRead.Should().Be(0);
    }

    [Fact]
    public async Task NestingTenThousandDeep_Answers400() {
        await using var harness = DavTestHarness.Create(options: o => o.MaxBodyBytes = 4 * 1024 * 1024);
        var builder = new StringBuilder("<D:propfind xmlns:D=\"DAV:\"><D:prop>");
        for (var i = 0; i < 10_000; i++) builder.Append("<D:x>");
        for (var i = 0; i < 10_000; i++) builder.Append("</D:x>");
        builder.Append("</D:prop></D:propfind>");

        (await harness.SendAsync("PROPFIND", "/", builder.ToString())).Status.Should().Be(400);
    }

    [Fact]
    public async Task MoreElementsThanTheLimit_Answer413() {
        await using var harness = DavTestHarness.Create(options: o => o.MaxXmlElements = 100);
        var body = "<D:propfind xmlns:D=\"DAV:\"><D:prop>" + string.Concat(Enumerable.Repeat("<D:displayname/>", 200)) + "</D:prop></D:propfind>";

        (await harness.SendAsync("PROPFIND", "/", body)).Status.Should().Be(413);
    }

    [Fact]
    public async Task MalformedXml_Answers400() {
        await using var harness = DavTestHarness.Create();

        (await harness.SendAsync("PROPFIND", "/", "<D:propfind xmlns:D=\"DAV:\"><D:prop>")).Status.Should().Be(400);
        (await harness.SendAsync("REPORT", "/", "not xml")).Status.Should().Be(400);
    }

    [Fact]
    public void TheParser_ReportsEveryLimitAsAResult() {
        var limits = new DavXmlLimits(1024, 3, 5);

        DavXml.Parse("<a><b><c><d/></c></b></a>"u8.ToArray(), limits).Error.Should().Be(DavXmlErrorKind.TooDeep);
        DavXml.Parse("<a><b/><b/><b/><b/><b/></a>"u8.ToArray(), limits).Error.Should().Be(DavXmlErrorKind.TooLarge);
        DavXml.Parse(new byte[2048], limits).Error.Should().Be(DavXmlErrorKind.TooLarge);
        DavXml.Parse("<a>"u8.ToArray(), limits).Error.Should().Be(DavXmlErrorKind.Malformed);
        DavXml.Parse("<a x=\"1\"><b>t</b></a>"u8.ToArray(), limits).Root!.Attribute("x").Should().Be("1");
    }

    /// <summary>A stream of zeros that counts what was read.</summary>
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
            Array.Fill(buffer, (byte)'<', offset, n);
            BytesRead += n;
            return n;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
