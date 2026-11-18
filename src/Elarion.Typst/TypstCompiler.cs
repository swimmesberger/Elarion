using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Elarion.Typst;

/// <summary>The CLI-backed <see cref="ITypstCompiler"/>.</summary>
internal sealed class TypstCompiler : ITypstCompiler, IDisposable {
    private readonly TypstOptions _options;
    private readonly TypstToolchain _toolchain;
    private readonly ILogger<TypstCompiler> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly string _workingDirectory;

    public TypstCompiler(TypstOptions options, TypstToolchain toolchain, ILogger<TypstCompiler> logger) {
        _options = options;
        _toolchain = toolchain;
        _logger = logger;
        _slots = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        _workingDirectory = Path.GetFullPath(options.WorkingDirectory ?? Path.Combine(Path.GetTempPath(), "elarion-typst"));
    }

    public async Task<TypstPdf> CompilePdfAsync(TypstCompileRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        using var activity = TypstTelemetry.Source.StartActivity("typst compile");
        activity?.SetTag(TypstTelemetry.DocumentTag, request.Source.Description);

        var queued = Stopwatch.GetTimestamp();
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            TypstTelemetry.RecordQueue(Stopwatch.GetElapsedTime(queued));
            Directory.CreateDirectory(_workingDirectory);
            using var workspace = await TypstWorkspace.CreateAsync(_workingDirectory, request, _logger, cancellationToken)
                .ConfigureAwait(false);
            try {
                var warnings = await RunAsync(workspace, request, activity, cancellationToken).ConfigureAwait(false);
                // DeleteOnClose: the output file goes when the caller disposes the TypstPdf. FileShare.Delete lets the
                // workspace cleanup and a crashed-process sweep on Windows proceed while the stream is open.
                var content = new FileStream(
                    workspace.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                return new TypstPdf(content, warnings);
            } catch {
                workspace.DeleteOutput();
                throw;
            }
        } finally {
            _slots.Release();
        }
    }

    /// <summary>Runs <c>typst compile</c> once and returns its warnings; throws on every failure.</summary>
    private async Task<string> RunAsync(
        TypstWorkspace workspace, TypstCompileRequest request, Activity? activity, CancellationToken cancellationToken) {
        var timeout = request.Timeout ?? _options.Timeout;
        var startInfo = CreateStartInfo(workspace.Root, workspace.OutputPath, request);

        var started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        TypstRun run;
        try {
            run = await TypstProcess.RunAsync(startInfo, deadline.Token).ConfigureAwait(false);
        } catch (Win32Exception ex) {
            Complete(activity, TypstOutcome.Unavailable, started);
            _logger.LogError(ex, "Typst could not be started for {Document}: {Reason}", request.Source.Description,
                _toolchain.UnavailableMessage());
            throw new TypstUnavailableException(_toolchain.UnavailableMessage(), ex);
        } catch (OperationCanceledException) when (deadline.IsCancellationRequested) {
            if (cancellationToken.IsCancellationRequested) {
                // The caller went away; not an error worth logging as one.
                Complete(activity, TypstOutcome.Cancelled, started);
                throw;
            }

            Complete(activity, TypstOutcome.Timeout, started);
            _logger.LogError("Typst did not compile {Document} within {Timeout}; the process was killed",
                request.Source.Description, timeout);
            throw new TypstTimeoutException($"Typst did not compile {request.Source.Description} within {timeout}.", timeout);
        }

        var errors = run.StandardError.Trim();
        var output = run.StandardOutput.Trim();
        if (run.ExitCode != 0) {
            Complete(activity, TypstOutcome.CompilationError, started);
            // A process ended by a signal printed nothing useful: SIGKILL usually means the kernel's out-of-memory killer.
            var termination = run.Signal is { } signal ? $"Typst was terminated by {signal}." : "";
            var diagnostics = string.Join(Environment.NewLine, new[] { termination, errors, output }.Where(s => s.Length > 0));
            _logger.LogError("Typst rejected {Document} with exit code {ExitCode}: {Diagnostics}",
                request.Source.Description, run.ExitCode, diagnostics);
            throw new TypstCompilationException(
                $"Typst rejected {request.Source.Description} with exit code {run.ExitCode}.", run.ExitCode, diagnostics);
        }

        if (!File.Exists(workspace.OutputPath)) {
            Complete(activity, TypstOutcome.CompilationError, started);
            throw new TypstCompilationException(
                $"Typst reported success for {request.Source.Description} but wrote no output.", run.ExitCode, errors);
        }

        Complete(activity, TypstOutcome.Success, started);
        if (errors.Length > 0) {
            _logger.LogWarning("Typst compiled {Document} with warnings: {Diagnostics}", request.Source.Description, errors);
        }

        _logger.LogDebug("Typst compiled {Document} in {Elapsed}", request.Source.Description, Stopwatch.GetElapsedTime(started));
        return errors;
    }

    internal ProcessStartInfo CreateStartInfo(string root, string outputPath, TypstCompileRequest request) {
        var startInfo = new ProcessStartInfo(_toolchain.ExecutablePath) {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // The options are the only configuration: inherited TYPST_FONT_PATHS, TYPST_PACKAGE_PATH, SOURCE_DATE_EPOCH and
        // friends would otherwise change a document depending on the machine it renders on.
        foreach (var key in startInfo.Environment.Keys.ToList()) {
            if (key.StartsWith("TYPST_", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "SOURCE_DATE_EPOCH", StringComparison.OrdinalIgnoreCase)) {
                startInfo.Environment.Remove(key);
            }
        }

        var arguments = startInfo.ArgumentList;
        arguments.Add("compile");
        arguments.Add("--root");
        arguments.Add(root);
        if (_toolchain.PackagePath is { } packagePath) {
            arguments.Add("--package-path");
            arguments.Add(packagePath);
        }

        if (_toolchain.PackageCachePath is { } packageCachePath) {
            arguments.Add("--package-cache-path");
            arguments.Add(packageCachePath);
        }

        foreach (var fontPath in _toolchain.FontPaths) {
            arguments.Add("--font-path");
            arguments.Add(fontPath);
        }

        if (_options.IgnoreSystemFonts) arguments.Add("--ignore-system-fonts");

        var standards = request.PdfStandards ?? _options.PdfStandards.ToList();
        if (standards.Count > 0) {
            arguments.Add("--pdf-standard");
            arguments.Add(string.Join(',', standards.Distinct().Select(standard => standard.ToCliValue())));
        }

        if (!_options.PdfTags) arguments.Add("--no-pdf-tags");

        if (request.CreationTimestamp is { } creation) {
            arguments.Add("--creation-timestamp");
            arguments.Add(creation.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }

        if (_options.Jobs is { } jobs) {
            arguments.Add("--jobs");
            arguments.Add(jobs.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var (key, value) in request.Inputs.OrderBy(input => input.Key, StringComparer.Ordinal)) {
            arguments.Add("--input");
            arguments.Add($"{key}={value}");
        }

        arguments.Add("--format");
        arguments.Add("pdf");
        arguments.Add(TypstWorkspace.MainFileName);
        arguments.Add(outputPath);
        return startInfo;
    }

    private static void Complete(Activity? activity, string outcome, long started) {
        TypstTelemetry.RecordCompile(outcome, Stopwatch.GetElapsedTime(started));
        if (activity is null) return;
        activity.SetTag(TypstTelemetry.OutcomeTag, outcome);
        if (outcome != TypstOutcome.Success && outcome != TypstOutcome.Cancelled) {
            activity.SetStatus(ActivityStatusCode.Error, outcome);
        }
    }

    public void Dispose() => _slots.Dispose();
}
