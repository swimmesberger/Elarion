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

    [Fact]
    public void Has_package_looks_for_the_manifest_in_the_package_directory() {
        var packages = Path.Combine(Base, "typst", "packages");
        var manifest = Path.Combine(packages, "local", "acme-documents", "1.0.0", "typst.toml");
        var toolchain = new TypstToolchain(new TypstOptions(), "", Base, path => path == manifest, path => path == packages);

        toolchain.HasPackage("@local/acme-documents:1.0.0").Should().BeTrue();
        toolchain.HasPackage("@local/acme-documents:1.0.1").Should().BeFalse();
        toolchain.HasPackage("@preview/cetz:0.3.4").Should().BeFalse("a server vendors @preview packages instead of downloading them");
    }

    [Fact]
    public void Has_package_is_false_without_a_package_directory() {
        var toolchain = new TypstToolchain(new TypstOptions(), "", Base, _ => true, _ => false);

        toolchain.PackagePath.Should().BeNull();
        toolchain.HasPackage("@local/acme-documents:1.0.0").Should().BeFalse();
    }

    [Theory]
    [InlineData("acme-documents:1.0.0")]
    [InlineData("@local/acme-documents")]
    [InlineData("@local/../acme:1.0.0")]
    public void Has_package_rejects_a_malformed_specification(string package) {
        var toolchain = new TypstToolchain(new TypstOptions(), "", Base, _ => true, _ => true);

        var act = () => toolchain.HasPackage(package);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Fail_check_reports_a_missing_required_package() {
        var options = new TypstOptions { StartupCheck = TypstStartupCheck.Fail };
        options.RequiredPackages.Add("@local/acme-documents:1.0.0");
        var service = new TypstStartupCheckService(
            options, new TypstToolchain(options, "", Base, _ => true, _ => false), NullLogger<TypstStartupCheckService>.Instance);

        var act = () => service.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<TypstUnavailableException>()).Which.Message.Should()
            .Contain("@local/acme-documents:1.0.0").And.Contain("ElarionTypstAssetsDirectory");
    }

    [Fact]
    public void Startup_check_accepts_an_installed_required_package() {
        var packages = Path.Combine(Base, "typst", "packages");
        var manifest = Path.Combine(packages, "local", "acme-documents", "1.0.0", "typst.toml");
        var options = new TypstOptions();
        options.RequiredPackages.Add("@local/acme-documents:1.0.0");
        var toolchain = new TypstToolchain(options, "", Base, path => path == manifest || path == BundledExecutable, path => path == packages);
        var service = new TypstStartupCheckService(options, toolchain, NullLogger<TypstStartupCheckService>.Instance);

        service.FindProblems().Should().BeEmpty();

        options.RequiredPackages.Add("@local/acme-documents:2.0.0");
        service.FindProblems().Should().ContainSingle().Which.Should().Contain(Path.Combine(packages, "local", "acme-documents", "2.0.0"));
    }

    [Fact]
    public void A_malformed_required_package_is_rejected_at_registration() {
        var act = () => new ServiceCollection().AddElarionTypst(options => options.RequiredPackages.Add("acme-documents"));

        act.Should().Throw<ArgumentException>().WithMessage("*RequiredPackages*");
    }

    [Fact]
    public void Code_defaults_in_a_first_call_yield_to_configuration_bound_in_a_second() {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Typst:Timeout"] = "00:00:45",
            ["Typst:RequiredPackages:0"] = "@local/acme-documents:1.0.0"
        }).Build();
        var services = new ServiceCollection();

        services.AddElarionTypst(options => {
            options.Timeout = TimeSpan.FromSeconds(30);
            options.StartupCheck = TypstStartupCheck.Warn;
        });
        services.AddElarionTypst(configuration.GetSection(TypstOptions.SectionName));

        var options = (TypstOptions)services.Single(descriptor => descriptor.ServiceType == typeof(TypstOptions)).ImplementationInstance!;
        options.Timeout.Should().Be(TimeSpan.FromSeconds(45), "configuration bound in the second call overrides the code default");
        options.StartupCheck.Should().Be(TypstStartupCheck.Warn, "a code default configuration does not set stays");
        options.RequiredPackages.Should().Equal("@local/acme-documents:1.0.0");
    }

    [Fact]
    public void Compiler_factory_builds_a_compiler_without_a_service_provider() {
        var options = new TypstOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-typst") };

        var compiler = TypstCompiler.Create(options);

        compiler.Should().BeAssignableTo<ITypstCompiler>();
        // A per-test kit creates one compiler per test and drops it: there is nothing to release.
        compiler.Should().NotBeAssignableTo<IDisposable>().And.NotBeAssignableTo<IAsyncDisposable>();
        var invalid = () => TypstCompiler.Create(new TypstOptions { MaxConcurrency = 0 });
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }
}
