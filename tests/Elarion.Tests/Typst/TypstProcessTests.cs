using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Typst;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Typst;

/// <summary>
/// Drives the compiler against stand-in executables (shell scripts) that misbehave the way a real process can: flooding
/// standard error, dying from a signal, hanging. Unix only: the stand-ins are <c>sh</c> scripts.
/// </summary>
public sealed class TypstProcessTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elarion-typst-process-tests", Guid.CreateVersion7().ToString("N"));

    public TypstProcessTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Diagnostics_larger_than_a_pipe_buffer_do_not_deadlock() {
        // 2 MB on stderr: a reader that waited for the exit before draining would block the child forever.
        var compiler = CreateCompiler("head -c 2097152 /dev/zero | tr '\\0' 'e' >&2; exit 1");

        var act = () => compiler.CompilePdfAsync(Request(), TestContext.Current.CancellationToken);

        var exception = (await act.Should().ThrowAsync<TypstCompilationException>()).Which;
        exception.ExitCode.Should().Be(1);
        exception.Diagnostics.Length.Should().BeGreaterThanOrEqualTo(2 * 1024 * 1024);
    }

    [Fact]
    public async Task A_process_killed_by_a_signal_is_reported() {
        var compiler = CreateCompiler("kill -9 $$");

        var act = () => compiler.CompilePdfAsync(Request(), TestContext.Current.CancellationToken);

        var exception = (await act.Should().ThrowAsync<TypstCompilationException>()).Which;
        exception.ExitCode.Should().Be(137);
#if NET11_0_OR_GREATER
        exception.Diagnostics.Should().Contain("SIGKILL", ".NET 11 reports the terminating signal");
#endif
    }

    [Fact]
    public async Task A_hanging_process_is_killed_at_the_timeout() {
        var compiler = CreateCompiler("exec sleep 30");
        var stopwatch = Stopwatch.StartNew();

        var act = () => compiler.CompilePdfAsync(Request() with { Timeout = TimeSpan.FromMilliseconds(300) },
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TypstTimeoutException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the process is killed, not awaited");
    }

    [Fact]
    public async Task Version_check_reads_standard_output() {
        var script = WriteScript("echo 'typst 9.9.9 (stand-in)'");

        var version = await new TypstToolchain(new TypstOptions { ExecutablePath = script })
            .GetVersionAsync(TestContext.Current.CancellationToken);

        version.Should().Be("typst 9.9.9 (stand-in)");
    }

    private TypstCompiler CreateCompiler(string body) {
        var options = new TypstOptions { ExecutablePath = WriteScript(body), WorkingDirectory = Path.Combine(_directory, "work") };
        return new TypstCompiler(options, new TypstToolchain(options), NullLogger<TypstCompiler>.Instance);
    }

    private string WriteScript(string body) {
        if (OperatingSystem.IsWindows()) Assert.Skip("The stand-in executables are sh scripts.");
        var path = Path.Combine(_directory, $"typst-{Guid.CreateVersion7():N}");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static TypstCompileRequest Request() => new() { Source = TypstSource.Main("#[x]") };
}
