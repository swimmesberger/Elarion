using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Typst;

/// <summary>
/// Reports an incomplete Typst toolchain at startup instead of on the first document request
/// (<see cref="TypstOptions.StartupCheck"/>).
/// </summary>
/// <remarks>
/// The file checks run on the start path: they are cheap and a missing file is certain to fail every document.
/// <c>typst --version</c> runs beside the start, not on its path: it starts a large executable, which a virus scanner
/// may hold up for seconds, and nothing in the start needs its answer. Under <see cref="TypstStartupCheck.Fail"/> a
/// failing version check ends <see cref="BackgroundService.ExecuteTask"/>, and the host's default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c> stops the host with the exception logged.
/// </remarks>
internal sealed class TypstStartupCheckService(
    TypstOptions options, TypstToolchain toolchain, ILogger<TypstStartupCheckService> logger) : BackgroundService {
    public override Task StartAsync(CancellationToken cancellationToken) {
        if (options.StartupCheck == TypstStartupCheck.None) return Task.CompletedTask;

        var problems = FindProblems();
        if (problems.Count > 0) {
            var message = "The Typst toolchain is incomplete: " + string.Join(" ", problems);
            if (options.StartupCheck == TypstStartupCheck.Fail) throw new TypstUnavailableException(message);
            logger.LogWarning("{Problems} Typst compilations will fail until this is fixed.", message);
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!toolchain.ExecutableExists) return;

        string version;
        try {
            version = await toolchain.GetVersionAsync(stoppingToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            return;
        } catch (TypstUnavailableException ex) when (options.StartupCheck == TypstStartupCheck.Warn) {
            logger.LogWarning(ex, "The Typst executable {Executable} does not run; Typst compilations will fail",
                toolchain.ExecutablePath);
            return;
        }

        logger.LogInformation(
            "Typst compilations use {Version} at {Executable} (packages: {PackagePath}; fonts: {FontPaths})",
            version, toolchain.ExecutablePath, toolchain.PackagePath ?? "<none>", string.Join(Path.PathSeparator, toolchain.FontPaths));
    }

    internal List<string> FindProblems() {
        var problems = new List<string>();
        if (!toolchain.ExecutableExists) problems.Add(toolchain.UnavailableMessage());
        if (options.PackagePath is { Length: > 0 } && toolchain.PackagePath is { } packagePath && !Directory.Exists(packagePath))
            problems.Add($"The configured package directory '{packagePath}' does not exist.");
        foreach (var fontPath in toolchain.FontPaths) {
            if (!Directory.Exists(fontPath)) problems.Add($"The configured font directory '{fontPath}' does not exist.");
        }

        return problems;
    }
}
