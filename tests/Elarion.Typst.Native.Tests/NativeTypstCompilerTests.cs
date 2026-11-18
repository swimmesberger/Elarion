using System.Diagnostics;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Typst;
using Elarion.Typst.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Typst.Native.Tests;

/// <summary>
/// The in-process engine against the shared test package (<c>tests/Elarion.Tests/Typst/Assets</c>), whose templates
/// assert on what they receive. Conformance tests compile the same request with the CLI engine and compare.
/// </summary>
public sealed class NativeTypstCompilerTests : IClassFixture<NativeEngineFixture> {
    private const string Package = "@local/elarion-test:1.0.0";

    private static readonly byte[] Logo = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly NativeEngineFixture _fixture;

    public NativeTypstCompilerTests(NativeEngineFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Package_source_receives_data_assets_and_inputs() {
        await using var pdf = await _fixture.Compiler().CompilePdfAsync(InvoiceRequest(), TestContext.Current.CancellationToken);

        (await ReadAllAsync(pdf)).AsSpan(0, 5).ToArray().Should().Equal("%PDF-"u8.ToArray());
        pdf.Warnings.Should().BeNull();
    }

    [Fact]
    public async Task Package_arguments_reach_the_entry_point() {
        await using var pdf = await _fixture.Compiler().CompilePdfAsync(new TypstCompileRequest {
            Source = TypstSource.Package(Package, "arguments")
                .WithArgument("kind", "quote \" and \\ backslash\nnext line")
                .WithArgument("chrome", false)
                .WithArgument("copies", 3)
                .WithArgument("total", 1280.50m)
        }, TestContext.Current.CancellationToken);

        pdf.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Main_source_reads_the_in_memory_project_files() {
        await using var pdf = await _fixture.Compiler().CompilePdfAsync(new TypstCompileRequest {
            Source = TypstSource.Main("""
                #let data = json("/data.json")
                #assert.eq(data.greeting, "hello")
                #image(read("/assets/logo.png", encoding: none), width: 1cm)
                """),
            Data = TypstData.FromJson("""{ "greeting": "hello" }"""),
            Assets = [new TypstAsset("logo.png", Logo)]
        }, TestContext.Current.CancellationToken);

        pdf.Length.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_is_byte_identical_to_the_cli_engine(bool pdfA) {
        var request = InvoiceRequest() with {
            CreationTimestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            PdfStandards = pdfA ? [TypstPdfStandard.PdfA2b] : null
        };

        await using var native = await _fixture.Compiler().CompilePdfAsync(request, TestContext.Current.CancellationToken);
        await using var cli = await _fixture.CliCompiler().CompilePdfAsync(request, TestContext.Current.CancellationToken);

        (await ReadAllAsync(native)).Should().Equal(await ReadAllAsync(cli));
    }

    [Fact]
    public async Task Template_error_reports_structured_diagnostics() {
        var act = () => _fixture.Compiler().CompilePdfAsync(
            new TypstCompileRequest { Source = TypstSource.Package(Package, "broken") }, TestContext.Current.CancellationToken);

        var exception = (await act.Should().ThrowAsync<TypstCompilationException>()).Which;
        exception.ExitCode.Should().BeNull("the document was compiled in-process");
        var error = exception.Errors.Should().ContainSingle(entry => entry.Severity == "error").Which;
        error.Message.Should().Contain("the template is broken");
        error.File.Should().Be("@local/elarion-test:1.0.0/lib.typ");
        error.Line.Should().BePositive();
        error.Column.Should().BePositive();
        exception.Diagnostics.Should().Contain("lib.typ:");
    }

    [Fact]
    public async Task Missing_package_is_a_compilation_error_not_a_download() {
        var act = () => _fixture.Compiler().CompilePdfAsync(
            new TypstCompileRequest { Source = TypstSource.Package("@preview/not-vendored:9.9.9", "anything") },
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<TypstCompilationException>()).Which.Diagnostics.Should().Contain("not-vendored");
    }

    [Fact]
    public async Task Unknown_font_is_reported_as_a_warning() {
        await using var pdf = await _fixture.Compiler().CompilePdfAsync(
            new TypstCompileRequest { Source = TypstSource.Package(Package, "unknown-font") }, TestContext.Current.CancellationToken);

        pdf.Warnings.Should().Contain("unknown font family");
    }

    [Fact]
    public async Task Concurrent_compilations_share_one_engine() {
        var compiler = _fixture.Compiler(options => options.MaxConcurrency = 4);

        var pdfs = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            compiler.CompilePdfAsync(InvoiceRequest(), TestContext.Current.CancellationToken)));

        foreach (var pdf in pdfs) {
            pdf.Length.Should().BeGreaterThan(0);
            await pdf.DisposeAsync();
        }
    }

    [Fact]
    public async Task Timeout_abandons_the_compilation_and_keeps_its_slot_until_it_ends() {
        var compiler = _fixture.Compiler(options => options.MaxConcurrency = 1);
        var stopwatch = Stopwatch.StartNew();

        var act = () => compiler.CompilePdfAsync(
            new TypstCompileRequest { Source = TypstSource.Package(Package, "slow"), Timeout = TimeSpan.FromMilliseconds(50) },
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TypstTimeoutException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), "the caller is released at the timeout");

        // The only slot is still held by the abandoned compilation; the next document waits for it, then succeeds.
        await using var next = await compiler.CompilePdfAsync(InvoiceRequest(), TestContext.Current.CancellationToken);
        next.Length.Should().BeGreaterThan(0);
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(300), "the abandoned compilation ran to its end first");
    }

    [Fact]
    public async Task Registration_replaces_the_cli_engine_and_checks_the_library_at_startup() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionTypstNative(options => options.WorkingDirectory = Path.GetTempPath());

        await using var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>()) await hosted.StartAsync(TestContext.Current.CancellationToken);

        provider.GetRequiredService<ITypstCompiler>().Should().BeOfType<NativeTypstCompiler>();
        provider.GetServices<IHostedService>().Should().ContainSingle().Which.Should().BeOfType<NativeTypstStartupCheck>();
        provider.GetRequiredService<NativeTypstEngine>().TypstVersion.Should().Be("0.15.1");
    }

    internal static TypstCompileRequest InvoiceRequest() => new() {
        Source = TypstSource.Package(Package, "invoice"),
        Data = TypstData.FromValue(
            new NativeInvoiceDocument { Number = "2026-0042", Items = [new() { Name = "Consulting", Amount = "1,200.00" }, new() { Name = "Travel", Amount = "80.00" }] },
            NativeTestJsonContext.Default.NativeInvoiceDocument),
        Assets = [new TypstAsset("logo.png", Logo)],
        Inputs = new Dictionary<string, string> { ["lang"] = "de" }
    };

    private static async Task<byte[]> ReadAllAsync(TypstPdf pdf) {
        using var buffer = new MemoryStream();
        await pdf.Content.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        return buffer.ToArray();
    }
}

/// <summary>One native engine for the class: creating it scans the fonts, which is the cost the engine amortizes.</summary>
public sealed class NativeEngineFixture : IDisposable {
    private readonly TypstOptions _options = new() { WorkingDirectory = Path.Combine(Path.GetTempPath(), "elarion-typst-native-tests") };
    private readonly NativeTypstEngine _engine;

    public NativeEngineFixture() {
        var toolchain = new TypstToolchain(_options);
        _engine = new NativeTypstEngine(_options, toolchain);
    }

    internal NativeTypstCompiler Compiler(Action<TypstOptions>? configure = null) {
        var options = new TypstOptions { WorkingDirectory = _options.WorkingDirectory };
        configure?.Invoke(options);
        return new NativeTypstCompiler(options, _engine, NullLogger<NativeTypstCompiler>.Instance);
    }

    internal TypstCompiler CliCompiler() {
        var toolchain = new TypstToolchain(_options);
        Assert.SkipUnless(toolchain.ExecutableExists, "The Typst CLI is not bundled.");
        return new TypstCompiler(_options, toolchain, NullLogger<TypstCompiler>.Instance);
    }

    public void Dispose() => _engine.Dispose();
}

internal sealed record NativeInvoiceDocument {
    public required string Number { get; init; }
    public required IReadOnlyList<NativeInvoiceItem> Items { get; init; }
}

internal sealed record NativeInvoiceItem {
    public required string Name { get; init; }
    public required string Amount { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NativeInvoiceDocument))]
internal sealed partial class NativeTestJsonContext : JsonSerializerContext;
