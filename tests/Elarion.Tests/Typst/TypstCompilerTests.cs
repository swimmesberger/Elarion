using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Typst;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Typst;

/// <summary>
/// Compiles real documents with the Typst binary the build bundles into the test output (the Elarion.Typst targets),
/// against the test package under <c>Typst/Assets</c>. The templates assert on what they receive, so a produced PDF
/// proves the data, the assets and the inputs arrived.
/// </summary>
public sealed class TypstCompilerTests : IDisposable {
    private const string Package = "@local/elarion-test:1.0.0";

    // A 1x1 PNG.
    private static readonly byte[] Logo = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), "elarion-typst-tests", Guid.CreateVersion7().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(_workingDirectory)) Directory.Delete(_workingDirectory, recursive: true);
    }

    [Fact]
    public async Task Package_source_receives_data_assets_and_inputs() {
        var compiler = CreateCompiler();

        await using var pdf = await compiler.CompilePdfAsync(InvoiceRequest(), TestContext.Current.CancellationToken);

        pdf.Length.Should().BeGreaterThan(0);
        (await ReadHeaderAsync(pdf)).Should().Be("%PDF-");
        pdf.Warnings.Should().BeNull();
    }

    [Fact]
    public async Task Package_source_without_data_passes_none_and_empty_assets() {
        var compiler = CreateCompiler();

        await using var pdf = await compiler.CompilePdfAsync(
            new TypstCompileRequest { Source = TypstSource.Package(Package, "plain") },
            TestContext.Current.CancellationToken);

        (await ReadHeaderAsync(pdf)).Should().Be("%PDF-");
    }

    [Fact]
    public async Task Package_arguments_reach_the_entry_point() {
        var compiler = CreateCompiler();

        await using var pdf = await compiler.CompilePdfAsync(new TypstCompileRequest {
            Source = TypstSource.Package(Package, "arguments")
                .WithArgument("kind", "quote \" and \\ backslash\nnext line")
                .WithArgument("chrome", false)
                .WithArgument("copies", 3)
                .WithArgument("total", 1280.50m)
        }, TestContext.Current.CancellationToken);

        (await ReadHeaderAsync(pdf)).Should().Be("%PDF-");
    }

    [Fact]
    public async Task Main_source_reads_the_workspace_files_by_absolute_path() {
        var compiler = CreateCompiler();

        await using var pdf = await compiler.CompilePdfAsync(new TypstCompileRequest {
            Source = TypstSource.Main("""
                #let data = json("/data.json")
                #assert.eq(data.greeting, "hello")
                #image(read("/assets/logo.png", encoding: none), width: 1cm)
                """),
            Data = TypstData.FromJson("""{ "greeting": "hello" }"""),
            Assets = [new TypstAsset("logo.png", Logo)]
        }, TestContext.Current.CancellationToken);

        (await ReadHeaderAsync(pdf)).Should().Be("%PDF-");
    }

    [Fact]
    public async Task Workspace_is_removed_and_output_deleted_on_dispose() {
        var compiler = CreateCompiler();

        var pdf = await compiler.CompilePdfAsync(InvoiceRequest(), TestContext.Current.CancellationToken);

        Directory.GetDirectories(_workingDirectory).Should().BeEmpty("the workspace is deleted when the compilation ends");
        Directory.GetFiles(_workingDirectory).Should().ContainSingle("the PDF lives until the caller disposes it");
        await pdf.DisposeAsync();
        Directory.GetFileSystemEntries(_workingDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task Template_error_throws_compilation_exception_with_diagnostics_and_cleans_up() {
        var compiler = CreateCompiler();

        var act = () => compiler.CompilePdfAsync(
            new TypstCompileRequest { Source = TypstSource.Package(Package, "broken") },
            TestContext.Current.CancellationToken);

        var exception = (await act.Should().ThrowAsync<TypstCompilationException>()).Which;
        exception.ExitCode.Should().NotBe(0);
        exception.Diagnostics.Should().Contain("the template is broken");
        Directory.GetFileSystemEntries(_workingDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_package_throws_package_not_found_exception() {
        var compiler = CreateCompiler();

        var act = () => compiler.CompilePdfAsync(
            new TypstCompileRequest { Source = TypstSource.Package("@local/elarion-missing:1.0.0", "invoice") },
            TestContext.Current.CancellationToken);

        var exception = (await act.Should().ThrowAsync<TypstPackageNotFoundException>()).Which;
        exception.Package.Should().Be("@local/elarion-missing:1.0.0");
        exception.ExitCode.Should().NotBe(0);
        exception.Diagnostics.Should().Contain("package not found");
        Directory.GetFileSystemEntries(_workingDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task Json_asset_is_a_second_model_the_template_parses() {
        var compiler = CreateCompiler();

        await using var pdf = await compiler.CompilePdfAsync(new TypstCompileRequest {
            Source = TypstSource.Main("""
                #let letterhead = json(read("/assets/letterhead.json", encoding: none))
                #assert.eq(letterhead.company, "Acme Gmbh & Co. KG")
                #letterhead.company
                """),
            Assets = [TypstAsset.FromJson("letterhead.json", new TypstLetterhead { Company = "Acme Gmbh & Co. KG" },
                TypstTestJsonContext.Default.TypstLetterhead)]
        }, TestContext.Current.CancellationToken);

        (await ReadHeaderAsync(pdf)).Should().Be("%PDF-");
    }

    [Fact]
    public async Task Compiler_factory_compiles_without_a_service_provider() {
        var options = new TypstOptions { WorkingDirectory = _workingDirectory };
        Assert.SkipUnless(new TypstToolchain(options).ExecutableExists, "Typst is not available.");
        using var compiler = TypstCompiler.Create(options, NullLoggerFactory.Instance);

        await using var pdf = await compiler.CompilePdfAsync(InvoiceRequest(), TestContext.Current.CancellationToken);

        (await ReadHeaderAsync(pdf)).Should().Be("%PDF-");
    }

    [Fact]
    public async Task Exceeded_timeout_kills_the_process_and_throws_timeout_exception() {
        var compiler = CreateCompiler();

        var act = () => compiler.CompilePdfAsync(
            InvoiceRequest() with { Timeout = TimeSpan.FromMilliseconds(1) },
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<TypstTimeoutException>()).Which.Timeout.Should().Be(TimeSpan.FromMilliseconds(1));
        Directory.GetFileSystemEntries(_workingDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_operation_canceled() {
        var compiler = CreateCompiler();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => compiler.CompilePdfAsync(InvoiceRequest(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Missing_executable_throws_unavailable_exception() {
        var options = new TypstOptions {
            ExecutablePath = Path.Combine(_workingDirectory, "no-such-typst"),
            WorkingDirectory = _workingDirectory
        };
        var compiler = new TypstCompiler(options, new TypstToolchain(options), NullLogger<TypstCompiler>.Instance);

        var act = () => compiler.CompilePdfAsync(InvoiceRequest(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<TypstUnavailableException>()).Which.Message.Should().Contain("no-such-typst");
    }

    [Fact]
    public async Task Fixed_creation_timestamp_produces_identical_bytes() {
        var compiler = CreateCompiler();
        var request = InvoiceRequest() with { CreationTimestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) };

        await using var first = await compiler.CompilePdfAsync(request, TestContext.Current.CancellationToken);
        await using var second = await compiler.CompilePdfAsync(request, TestContext.Current.CancellationToken);

        (await ReadAllAsync(first)).Should().Equal(await ReadAllAsync(second));
    }

    [Fact]
    public async Task Pdf_a_standard_is_enforced() {
        var compiler = CreateCompiler(options => options.PdfStandards.Add(TypstPdfStandard.PdfA2b));

        await using var pdf = await compiler.CompilePdfAsync(InvoiceRequest(), TestContext.Current.CancellationToken);

        var text = System.Text.Encoding.Latin1.GetString(await ReadAllAsync(pdf));
        text.Should().Contain("pdfaid:part", "a PDF/A document declares its conformance in the XMP metadata");
    }

    [Fact]
    public async Task Concurrent_compilations_beyond_the_limit_queue_and_all_complete() {
        var compiler = CreateCompiler(options => options.MaxConcurrency = 1);

        var pdfs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            compiler.CompilePdfAsync(InvoiceRequest(), TestContext.Current.CancellationToken)));

        foreach (var pdf in pdfs) {
            (await ReadHeaderAsync(pdf)).Should().Be("%PDF-");
            await pdf.DisposeAsync();
        }
    }

    [Fact]
    public async Task To_elarion_file_buffers_the_pdf() {
        var compiler = CreateCompiler();
        await using var pdf = await compiler.CompilePdfAsync(InvoiceRequest(), TestContext.Current.CancellationToken);

        var file = await pdf.ToElarionFileAsync("invoice.pdf", TestContext.Current.CancellationToken);

        file.ContentType.Should().Be("application/pdf");
        file.FileName.Should().Be("invoice.pdf");
        file.Bytes.Length.Should().Be((int)pdf.Length);
    }

    private TypstCompiler CreateCompiler(Action<TypstOptions>? configure = null) {
        var options = new TypstOptions { WorkingDirectory = _workingDirectory };
        configure?.Invoke(options);
        var toolchain = new TypstToolchain(options);
        Assert.SkipUnless(toolchain.ExecutableExists, $"Typst is not available at '{toolchain.ExecutablePath}'.");
        return new TypstCompiler(options, toolchain, NullLogger<TypstCompiler>.Instance);
    }

    private static TypstCompileRequest InvoiceRequest() => new() {
        Source = TypstSource.Package(Package, "invoice"),
        Data = TypstData.FromValue(
            new TypstInvoiceDocument { Number = "2026-0042", Items = [new() { Name = "Consulting", Amount = "1,200.00" }, new() { Name = "Travel", Amount = "80.00" }] },
            TypstTestJsonContext.Default.TypstInvoiceDocument),
        Assets = [new TypstAsset("logo.png", Logo)],
        Inputs = new Dictionary<string, string> { ["lang"] = "de" }
    };

    private static async Task<string> ReadHeaderAsync(TypstPdf pdf) {
        var header = new byte[5];
        await pdf.Content.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
        pdf.Content.Position = 0;
        return System.Text.Encoding.ASCII.GetString(header);
    }

    private static async Task<byte[]> ReadAllAsync(TypstPdf pdf) {
        using var buffer = new MemoryStream();
        await pdf.Content.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        return buffer.ToArray();
    }
}

internal sealed record TypstInvoiceDocument {
    public required string Number { get; init; }
    public required IReadOnlyList<TypstInvoiceItem> Items { get; init; }
}

internal sealed record TypstLetterhead {
    public required string Company { get; init; }
}

internal sealed record TypstInvoiceItem {
    public required string Name { get; init; }
    public required string Amount { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TypstInvoiceDocument))]
[JsonSerializable(typeof(TypstLetterhead))]
internal sealed partial class TypstTestJsonContext : JsonSerializerContext;
