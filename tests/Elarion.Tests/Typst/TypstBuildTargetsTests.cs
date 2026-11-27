using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Typst;
using Xunit;

namespace Elarion.Tests.Typst;

/// <summary>
/// Runs the <c>Elarion.Typst.targets</c> unpacker check through <c>dotnet msbuild</c> with a <c>PATH</c> of stand-in
/// tools, so each case sees exactly the <c>tar</c> and <c>xz</c> it describes. GNU tar without the xz program is the
/// state of the <c>mcr.microsoft.com/dotnet/sdk</c> images.
/// </summary>
public sealed class TypstBuildTargetsTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elarion-typst-targets", Guid.CreateVersion7().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Gnu_tar_without_xz_fails_before_the_download_with_the_remedy() {
        var result = await RunTargetAsync("ElarionAcquireTypst", tar: "tar (GNU tar) 1.35", xz: false);

        result.ExitCode.Should().NotBe(0, result.Output);
        result.Output.Should().Contain("apt-get install -y xz-utils").And.Contain("ElarionTypstExecutable");
        result.Output.Should().NotContain("Downloading");
        Directory.Exists(Path.Combine(_directory, "cache")).Should().BeFalse("the check runs before anything is downloaded");
    }

    [Fact]
    public async Task Gnu_tar_with_xz_passes_the_check() {
        var result = await RunTargetAsync("_ElarionTypstCheckUnpacker", tar: "tar (GNU tar) 1.35", xz: true);

        result.ExitCode.Should().Be(0, result.Output);
    }

    [Fact]
    public async Task Bsdtar_decompresses_xz_itself_and_passes_the_check() {
        var result = await RunTargetAsync("_ElarionTypstCheckUnpacker", tar: "bsdtar 3.5.3 - libarchive 3.7.4", xz: false);

        result.ExitCode.Should().Be(0, result.Output);
    }

    private async Task<(int ExitCode, string Output)> RunTargetAsync(string target, string tar, bool xz) {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The check runs on Unix build hosts; Windows' tar is bsdtar.");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath
            ? hostPath
            : TypstToolchain.ResolveExecutable("dotnet", Environment.GetEnvironmentVariable("PATH"), File.Exists);
        Assert.SkipWhen(dotnet is null, "dotnet is not on PATH.");
        var grep = new[] { "/usr/bin/grep", "/bin/grep" }.FirstOrDefault(File.Exists);
        Assert.SkipWhen(grep is null, "grep is not installed.");

        var tools = Path.Combine(_directory, "tools");
        Directory.CreateDirectory(tools);
        WriteScript(Path.Combine(tools, "tar"), $"echo '{tar}'");
        if (xz) WriteScript(Path.Combine(tools, "xz"), "exit 0");
        File.CreateSymbolicLink(Path.Combine(tools, "grep"), grep);
        // MSBuild's Exec looks its shell up on PATH.
        File.CreateSymbolicLink(Path.Combine(tools, "sh"), "/bin/sh");

        var project = Path.Combine(_directory, "probe.proj");
        await File.WriteAllTextAsync(project, $"""
            <Project>
              <PropertyGroup>
                <ElarionTypstBundle>true</ElarionTypstBundle>
                <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
                <ElarionTypstCacheDirectory>{Path.Combine(_directory, "cache")}</ElarionTypstCacheDirectory>
              </PropertyGroup>
              <Import Project="{TargetsPath()}"/>
              <Target Name="AssignTargetPaths"/>
            </Project>
            """, TestContext.Current.CancellationToken);

        var startInfo = new ProcessStartInfo(dotnet!) {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = _directory
        };
        foreach (var argument in new[] { "msbuild", project, $"-t:{target}", "-nologo", "-nodeReuse:false", "-v:m" })
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PATH"] = tools;
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output + await error);
    }

    private static void WriteScript(string path, string body) {
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string TargetsPath() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, "Elarion.slnx")))
                return Path.Combine(directory.FullName, "src", "Elarion.Typst", "buildTransitive", "Elarion.Typst.targets");
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}
