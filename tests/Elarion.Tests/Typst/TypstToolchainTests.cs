using AwesomeAssertions;
using Elarion.Typst;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Typst;

public sealed class TypstToolchainTests {
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "app");
    private static readonly string BundledExecutable = Path.Combine(Base, "typst", "bin", OperatingSystem.IsWindows() ? "typst.exe" : "typst");

    [Fact]
    public void Bundled_toolchain_is_the_default() {
        var existing = new HashSet<string> { BundledExecutable };
        var directories = new HashSet<string> { Path.Combine(Base, "typst", "packages"), Path.Combine(Base, "typst", "fonts") };

        var toolchain = new TypstToolchain(new TypstOptions(), "/usr/bin", Base, existing.Contains, directories.Contains);

        toolchain.ExecutablePath.Should().Be(BundledExecutable);
        toolchain.ExecutableExists.Should().BeTrue();
        toolchain.PackagePath.Should().Be(Path.Combine(Base, "typst", "packages"));
        toolchain.FontPaths.Should().Equal(Path.Combine(Base, "typst", "fonts"));
    }

    [Fact]
    public void Without_a_bundled_binary_typst_is_looked_up_on_path() {
        var onPath = Path.Combine("/usr/local/bin", "typst");

        var toolchain = new TypstToolchain(
            new TypstOptions(), $"/usr/bin{Path.PathSeparator}/usr/local/bin", Base, path => path == onPath, _ => false);

        toolchain.ExecutablePath.Should().Be(onPath);
        toolchain.PackagePath.Should().BeNull();
        toolchain.FontPaths.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_executable_keeps_the_configured_name_for_the_error_message() {
        var toolchain = new TypstToolchain(new TypstOptions(), "/usr/bin", Base, _ => false, _ => false);

        toolchain.ExecutableExists.Should().BeFalse();
        toolchain.ExecutablePath.Should().Be("typst");
        toolchain.UnavailableMessage().Should().Contain("ElarionTypstBundle");
    }

    [Fact]
    public void Configured_paths_win_over_the_bundled_layout() {
        var executable = Path.Combine(Path.GetTempPath(), "custom", "typst");
        var fonts = Path.Combine(Path.GetTempPath(), "custom-fonts");
        var options = new TypstOptions {
            ExecutablePath = executable,
            PackagePath = Path.Combine(Path.GetTempPath(), "custom-packages")
        };
        options.FontPaths.Add(fonts);

        var toolchain = new TypstToolchain(options, "", Base, path => path == executable || path == BundledExecutable, _ => true);

        toolchain.ExecutablePath.Should().Be(executable);
        toolchain.PackagePath.Should().Be(options.PackagePath);
        toolchain.FontPaths.Should().Equal(fonts, Path.Combine(Base, "typst", "fonts"));
    }

    [Fact]
    public void Windows_executable_sibling_is_found() {
        var windows = Path.Combine("/tools", "typst") + ".exe";

        TypstToolchain.ResolveExecutable("typst", "/tools", path => path == windows).Should().Be(windows);
    }

    [Fact]
    public async Task Fail_check_refuses_to_start_without_an_executable() {
        var options = new TypstOptions { StartupCheck = TypstStartupCheck.Fail };
        var service = new TypstStartupCheckService(
            options, new TypstToolchain(options, "", Base, _ => false, _ => false), NullLogger<TypstStartupCheckService>.Instance);

        var act = () => service.StartAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TypstUnavailableException>();
    }

    [Fact]
    public async Task Warn_check_starts_without_an_executable() {
        var options = new TypstOptions { StartupCheck = TypstStartupCheck.Warn };
        var service = new TypstStartupCheckService(
            options, new TypstToolchain(options, "", Base, _ => false, _ => false), NullLogger<TypstStartupCheckService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fail_check_reports_a_configured_font_directory_that_does_not_exist() {
        var options = new TypstOptions { StartupCheck = TypstStartupCheck.Fail };
        options.FontPaths.Add(Path.Combine(Path.GetTempPath(), "elarion-typst-missing-fonts", Guid.CreateVersion7().ToString("N")));
        var service = new TypstStartupCheckService(
            options, new TypstToolchain(options, "", Base, _ => true, _ => false), NullLogger<TypstStartupCheckService>.Instance);

        var act = () => service.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<TypstUnavailableException>()).Which.Message.Should().Contain("elarion-typst-missing-fonts");
    }

    [Fact]
    public async Task Bundled_toolchain_reports_its_version() {
        var toolchain = new TypstToolchain(new TypstOptions());
        Assert.SkipUnless(toolchain.ExecutableExists, $"Typst is not available at '{toolchain.ExecutablePath}'.");

        var version = await toolchain.GetVersionAsync(TestContext.Current.CancellationToken);

        version.Should().StartWith("typst ");
    }

    [Fact]
    public void Repeated_registration_composes_onto_one_options_instance() {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddElarionTypst(options => options.Jobs = 2);
        services.AddElarionTypst(options => options.PdfStandards.Add(TypstPdfStandard.PdfA2b));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<TypstOptions>();
        options.Jobs.Should().Be(2);
        options.PdfStandards.Should().Equal(TypstPdfStandard.PdfA2b);
        provider.GetRequiredService<ITypstCompiler>().Should().BeOfType<TypstCompiler>();
        provider.GetServices<IHostedService>().OfType<TypstStartupCheckService>().Should().ContainSingle();
    }

    [Fact]
    public void Options_bind_from_configuration_before_the_configure_delegate() {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Typst:ExecutablePath"] = "/opt/typst/bin/typst",
            ["Typst:Timeout"] = "00:00:30",
            ["Typst:MaxConcurrency"] = "2",
            ["Typst:Jobs"] = "1",
            ["Typst:IgnoreSystemFonts"] = "false",
            ["Typst:StartupCheck"] = "Warn",
            ["Typst:FontPaths:0"] = "/fonts/a",
            ["Typst:FontPaths:1"] = "/fonts/b",
            ["Typst:PdfStandards:0"] = "PdfA2b"
        }).Build();
        var services = new ServiceCollection();

        services.AddElarionTypst(configuration.GetSection(TypstOptions.SectionName), options => options.MaxConcurrency = 4);

        var options = (TypstOptions)services.Single(descriptor => descriptor.ServiceType == typeof(TypstOptions)).ImplementationInstance!;
        options.ExecutablePath.Should().Be("/opt/typst/bin/typst");
        options.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        options.MaxConcurrency.Should().Be(4, "the delegate runs after binding");
        options.Jobs.Should().Be(1);
        options.IgnoreSystemFonts.Should().BeFalse();
        options.StartupCheck.Should().Be(TypstStartupCheck.Warn);
        options.FontPaths.Should().Equal("/fonts/a", "/fonts/b");
        options.PdfStandards.Should().Equal(TypstPdfStandard.PdfA2b);
    }

    [Fact]
    public void Binding_an_invalid_value_is_rejected_at_registration() {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Typst:MaxConcurrency"] = "0"
        }).Build();

        var act = () => new ServiceCollection().AddElarionTypst(configuration.GetSection(TypstOptions.SectionName));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Invalid_options_are_rejected_at_registration() {
        var act = () => new ServiceCollection().AddElarionTypst(options => options.MaxConcurrency = 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
