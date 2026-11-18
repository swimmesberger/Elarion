using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Elarion.Typst;

/// <summary>
/// One compilation's project root: <c>main.typ</c>, <c>data.json</c>, <c>assets/</c>. It lives below the working
/// directory, never inside the template tree, and is deleted when the compilation ends.
/// </summary>
internal sealed class TypstWorkspace : IDisposable {
    public const string MainFileName = "main.typ";
    public const string DataFileName = "data.json";
    public const string AssetsDirectoryName = "assets";

    private readonly ILogger _logger;

    private TypstWorkspace(string root, string outputPath, ILogger logger) {
        Root = root;
        OutputPath = outputPath;
        _logger = logger;
    }

    /// <summary>The project root passed as <c>--root</c>.</summary>
    public string Root { get; }

    /// <summary>
    /// Where Typst writes the PDF: beside the workspace rather than in it, so the workspace can be deleted while the
    /// returned <see cref="TypstPdf"/> still holds the output open.
    /// </summary>
    public string OutputPath { get; }

    public static async Task<TypstWorkspace> CreateAsync(
        string workingDirectory, TypstCompileRequest request, ILogger logger, CancellationToken cancellationToken) {
        var id = Guid.CreateVersion7().ToString("N");
        var root = Path.Combine(workingDirectory, id);
        Directory.CreateDirectory(root);
        var workspace = new TypstWorkspace(root, Path.Combine(workingDirectory, id + ".pdf"), logger);
        try {
            if (request.Data is not null) {
                var dataFile = new FileStream(
                    Path.Combine(root, DataFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 16 * 1024, FileOptions.Asynchronous);
                await using (dataFile.ConfigureAwait(false)) {
                    await request.Data.WriteToAsync(dataFile, cancellationToken).ConfigureAwait(false);
                }
            }

            if (request.Assets.Count > 0) {
                var assets = Path.Combine(root, AssetsDirectoryName);
                Directory.CreateDirectory(assets);
                foreach (var asset in request.Assets) {
                    await File.WriteAllBytesAsync(Path.Combine(assets, asset.Name), asset.Content, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var mainFile = request.Source.RenderMainFile(request.Data is not null, request.Assets);
            await File.WriteAllTextAsync(Path.Combine(root, MainFileName), mainFile, cancellationToken).ConfigureAwait(false);
            return workspace;
        } catch {
            workspace.DeleteOutput();
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>Deletes an output file that will not be handed to a caller.</summary>
    public void DeleteOutput() => TryDelete(() => File.Delete(OutputPath), OutputPath);

    public void Dispose() => TryDelete(() => Directory.Delete(Root, recursive: true), Root);

    private void TryDelete(Action delete, string path) {
        try {
            delete();
        } catch (DirectoryNotFoundException) {
            // Already gone.
        } catch (IOException ex) {
            _logger.LogWarning(ex, "Could not delete the Typst workspace file {Path}", path);
        } catch (UnauthorizedAccessException ex) {
            _logger.LogWarning(ex, "Could not delete the Typst workspace file {Path}", path);
        }
    }
}

/// <summary>The outcome of one finished Typst process.</summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="StandardOutput">Everything written to standard output.</param>
/// <param name="StandardError">Everything written to standard error.</param>
/// <param name="Signal">The Unix signal that terminated the process, when one did (reported on .NET 11 and later).</param>
internal sealed record TypstRun(int ExitCode, string StandardOutput, string StandardError, string? Signal);

/// <summary>Runs Typst child processes: start, drain both pipes concurrently, wait, kill on cancellation.</summary>
internal static class TypstProcess {
    /// <summary>
    /// Runs <paramref name="startInfo"/> to completion. Cancelling <paramref name="cancellationToken"/> kills the process
    /// and throws <see cref="OperationCanceledException"/>; a start failure throws <see cref="Win32Exception"/>.
    /// </summary>
    public static async Task<TypstRun> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken) {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
#if NET11_0_OR_GREATER
        // .NET 11 hardens child processes for a long-running host:
        // - KillOnParentExit: a crashed or killed host leaves no Typst process behind (a Windows job object, Linux
        //   PR_SET_PDEATHSIG). macOS has no equivalent: there, the timeout and the cancellation are the only bound.
        // - InheritedHandles = []: the child receives its standard handles only, never the host's listening sockets or
        //   open files, which an inherited copy would keep open (a port that stays bound, a pipe that never sees EOF).
        //   It also lets parallel starts proceed without serializing on Windows.
        // - A null standard input: Typst reads none, and must not hold the host's.
        // RunAndCaptureTextAsync drains both pipes concurrently and kills the process when the token is cancelled.
        using var nullInput = File.OpenNullHandle();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) startInfo.KillOnParentExit = true;
        startInfo.InheritedHandles = [];
        startInfo.StandardInputHandle = nullInput;
        var result = await Process.RunAndCaptureTextAsync(startInfo, cancellationToken).ConfigureAwait(false);
        return new TypstRun(
            result.ExitStatus.ExitCode, result.StandardOutput, result.StandardError, result.ExitStatus.Signal?.ToString());
#else
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Both pipes are drained while the process runs: reading them only after the exit deadlocks as soon as Typst
        // writes more diagnostics than a pipe buffer holds. The reads end when the process exits or is killed.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            KillTree(process);
            await ObserveAsync(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        return new TypstRun(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), Signal: null);
#endif
    }

#if !NET11_0_OR_GREATER
    /// <summary>Awaits the pipe reads of a killed process so a broken-pipe fault is never left unobserved.</summary>
    private static async Task ObserveAsync(Task<string> stdout, Task<string> stderr) {
        try {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) {
            // The process was killed; its partial output is of no interest.
        }
    }

    /// <summary>Kills a process tree that may already have exited.</summary>
    private static void KillTree(Process process) {
        try {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        } catch (InvalidOperationException) {
            // It exited between the check and the kill.
        } catch (Win32Exception) {
            // It is exiting and can no longer be signalled.
        }
    }
#endif
}
