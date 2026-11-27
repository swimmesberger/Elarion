using AwesomeAssertions;
using Elarion.Typst;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Typst;

public sealed class TypstRequestTests {
    [Fact]
    public void Package_source_generates_a_main_file_that_hands_over_data_and_assets() {
        var source = TypstSource.Package("@local/acme-documents:1.2.3", "invoice");

        var main = source.RenderMainFile(hasData: true, [new TypstAsset("logo.png", new byte[] { 1 }), new TypstAsset("photo-0001.jpg", new byte[] { 2 })]);

        main.Should().Be(
            "#import \"@local/acme-documents:1.2.3\": invoice\n"
            + "#invoice(json(\"/data.json\"), assets: (\"logo.png\": read(\"/assets/logo.png\", encoding: none), "
            + "\"photo-0001.jpg\": read(\"/assets/photo-0001.jpg\", encoding: none), ))\n");
        source.Description.Should().Be("@local/acme-documents:1.2.3:invoice");
    }

    [Fact]
    public void Package_source_without_data_or_assets_passes_none_and_an_empty_dictionary() {
        TypstSource.Package("@local/acme-documents:1.0.0", "letter").RenderMainFile(hasData: false, [])
            .Should().Be("#import \"@local/acme-documents:1.0.0\": letter\n#letter(none, assets: (:))\n");
    }

    [Fact]
    public void Package_arguments_are_passed_as_typst_literals_between_data_and_assets() {
        var source = TypstSource.Package("@local/acme-reports:1.0.0", "render-report")
            .WithArgument("kind", "test-report")
            .WithArgument("chrome", false)
            .WithArgument("copies", 2)
            .WithArgument("total", 1280.50m);

        source.RenderMainFile(hasData: true, []).Should().Be(
            "#import \"@local/acme-reports:1.0.0\": render-report\n"
            + "#render-report(json(\"/data.json\"), kind: \"test-report\", chrome: false, copies: 2, "
            + "total: decimal(\"1280.50\"), assets: (:))\n");
    }

    [Fact]
    public void Package_arguments_leave_the_original_source_unchanged() {
        var source = TypstSource.Package("@local/acme-reports:1.0.0", "letter");

        _ = source.WithArgument("lang", "de");

        source.RenderMainFile(hasData: false, []).Should().NotContain("lang");
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("quote \" and \\ backslash", "\"quote \\\" and \\\\ backslash\"")]
    [InlineData("line\nbreak\ttab\r", "\"line\\nbreak\\ttab\\r\"")]
    [InlineData("bell\u0007", "\"bell\\u{7}\"")]
    [InlineData("#raw(\"x\") ü €", "\"#raw(\\\"x\\\") ü €\"")]
    public void String_arguments_are_escaped(string value, string literal) =>
        TypstSource.StringLiteral(value).Should().Be(literal);

    [Theory]
    [InlineData("assets")]
    [InlineData("1kind")]
    [InlineData("kind: \"x\"")]
    public void Package_arguments_reject_names_that_are_not_identifiers_or_are_reserved(string name) {
        var act = () => TypstSource.Package("@local/acme-reports:1.0.0", "letter").WithArgument(name, "x");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Package_arguments_reject_a_duplicate_name() {
        var act = () => TypstSource.Package("@local/acme-reports:1.0.0", "letter").WithArgument("lang", "de").WithArgument("lang", "en");

        act.Should().Throw<ArgumentException>().WithMessage("*lang*");
    }

    [Fact]
    public void Main_source_takes_no_arguments() {
        var act = () => TypstSource.Main("#[x]").WithArgument("lang", "de");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Main_source_is_used_verbatim() {
        TypstSource.Main("#[hi]").RenderMainFile(hasData: true, [new TypstAsset("a.png", new byte[] { 1 })])
            .Should().Be("#[hi]");
    }

    [Theory]
    [InlineData("acme-documents:1.0.0")]
    [InlineData("@local/acme-documents")]
    [InlineData("@local/acme\": evil")]
    [InlineData("@local/acme-documents:1.0")]
    public void Package_source_rejects_a_malformed_specification(string package) {
        var act = () => TypstSource.Package(package, "invoice");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("1invoice")]
    [InlineData("invoice(x)")]
    [InlineData("in voice")]
    public void Package_source_rejects_a_function_that_is_not_an_identifier(string function) {
        var act = () => TypstSource.Package("@local/acme-documents:1.0.0", function);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("logo.png")]
    [InlineData("photo_0001-a.JPG")]
    public void Asset_accepts_a_plain_file_name(string name) {
        new TypstAsset(name, ReadOnlyMemory<byte>.Empty).Name.Should().Be(name);
        TypstAsset.IsValidName(name).Should().BeTrue();
        TypstAsset.TryCreate(name, ReadOnlyMemory<byte>.Empty, out var asset).Should().BeTrue();
        asset!.Name.Should().Be(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".hidden")]
    [InlineData("../escape.png")]
    [InlineData("dir/logo.png")]
    [InlineData("dir\\logo.png")]
    [InlineData("a..b")]
    [InlineData("logo\".png")]
    [InlineData("lögo.png")]
    public void Asset_rejects_a_name_that_could_escape_the_workspace_or_the_main_file(string name) {
        var act = () => new TypstAsset(name, ReadOnlyMemory<byte>.Empty);

        act.Should().Throw<ArgumentException>();
        TypstAsset.IsValidName(name).Should().BeFalse();
        TypstAsset.TryCreate(name, ReadOnlyMemory<byte>.Empty, out var asset).Should().BeFalse();
        asset.Should().BeNull();
    }

    [Fact]
    public void Asset_name_validation_rejects_null_and_names_longer_than_255_characters() {
        TypstAsset.IsValidName(null).Should().BeFalse();
        TypstAsset.IsValidName(new string('a', 255)).Should().BeTrue();
        TypstAsset.IsValidName(new string('a', 256)).Should().BeFalse();
    }

    [Fact]
    public void Json_asset_holds_the_utf8_serialization_of_the_model() {
        var asset = TypstAsset.FromJson(
            "letterhead.json", new TypstLetterhead { Company = "Acme Gmbh & Co. KG" }, TypstTestJsonContext.Default.TypstLetterhead);

        asset.Name.Should().Be("letterhead.json");
        System.Text.Encoding.UTF8.GetString(asset.Content.Span).Should().Be("""{"company":"Acme Gmbh \u0026 Co. KG"}""");
    }

    [Fact]
    public void Json_asset_rejects_an_invalid_name() {
        var act = () => TypstAsset.FromJson("../letterhead.json", new TypstLetterhead { Company = "Acme" }, TypstTestJsonContext.Default.TypstLetterhead);

        act.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Fact]
    public void Request_rejects_duplicate_asset_names() {
        var request = new TypstCompileRequest {
            Source = TypstSource.Main("#[x]"),
            Assets = [new TypstAsset("a.png", ReadOnlyMemory<byte>.Empty), new TypstAsset("a.png", ReadOnlyMemory<byte>.Empty)]
        };

        var act = request.Validate;

        act.Should().Throw<ArgumentException>().WithMessage("*a.png*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a=b")]
    public void Request_rejects_an_input_key_typst_cannot_parse(string key) {
        var request = new TypstCompileRequest {
            Source = TypstSource.Main("#[x]"),
            Inputs = new Dictionary<string, string> { [key] = "value" }
        };

        var act = request.Validate;

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Start_info_passes_every_option_and_strips_inherited_typst_environment() {
        var root = Path.Combine(Path.GetTempPath(), "typst-root");
        var fonts = Path.Combine(Path.GetTempPath(), "typst-fonts");
        var options = new TypstOptions {
            ExecutablePath = "/opt/typst/bin/typst",
            PackagePath = Path.Combine(Path.GetTempPath(), "typst-packages"),
            Jobs = 2,
            PdfTags = false
        };
        options.FontPaths.Add(fonts);
        options.PdfStandards.Add(TypstPdfStandard.PdfA2b);
        options.PdfStandards.Add(TypstPdfStandard.PdfUa1);
        var compiler = new TypstCompiler(
            options, new TypstToolchain(options, "", "/app", _ => false, _ => false), NullLogger<TypstCompiler>.Instance);
        Environment.SetEnvironmentVariable("TYPST_FONT_PATHS", "/somewhere/else");
        try {
            var startInfo = compiler.CreateStartInfo(root, "/tmp/out.pdf", new TypstCompileRequest {
                Source = TypstSource.Main("#[x]"),
                Inputs = new Dictionary<string, string> { ["lang"] = "de", ["brand"] = "a=b c" },
                CreationTimestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)
            });

            startInfo.WorkingDirectory.Should().Be(root);
            startInfo.ArgumentList.Should().Equal(
                "compile",
                "--root", root,
                "--package-path", options.PackagePath,
                "--font-path", fonts,
                "--ignore-system-fonts",
                "--pdf-standard", "a-2b,ua-1",
                "--no-pdf-tags",
                "--creation-timestamp", "1700000000",
                "--jobs", "2",
                "--input", "brand=a=b c",
                "--input", "lang=de",
                "--format", "pdf",
                "main.typ",
                "/tmp/out.pdf");
            startInfo.Environment.Keys.Should().NotContain("TYPST_FONT_PATHS");
        } finally {
            Environment.SetEnvironmentVariable("TYPST_FONT_PATHS", null);
        }
    }

    [Fact]
    public void Request_pdf_standards_override_the_options() {
        var options = new TypstOptions { ExecutablePath = "/opt/typst/bin/typst" };
        options.PdfStandards.Add(TypstPdfStandard.PdfA2b);
        var compiler = new TypstCompiler(
            options, new TypstToolchain(options, "", "/app", _ => false, _ => false), NullLogger<TypstCompiler>.Instance);

        var startInfo = compiler.CreateStartInfo("/root", "/out.pdf", new TypstCompileRequest {
            Source = TypstSource.Main("#[x]"),
            PdfStandards = []
        });

        startInfo.ArgumentList.Should().NotContain("--pdf-standard");
    }
}
