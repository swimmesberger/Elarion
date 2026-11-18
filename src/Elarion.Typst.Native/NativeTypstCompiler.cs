using System.Diagnostics;
using Elarion.Typst.Native.Interop;
using Microsoft.Extensions.Logging;

namespace Elarion.Typst.Native;

/// <summary>
/// The in-process <see cref="ITypstCompiler"/>: the same requests, templates and results as the CLI engine, compiled by
/// the embedded Rust library.
/// </summary>
/// <remarks>
/// <para>
/// A compilation runs synchronously inside the library, so each one gets a dedicated thread rather than a thread-pool
/// thread a burst of documents would starve. At most <see cref="TypstOptions.MaxConcurrency"/> run at once.
/// </para>
/// <para>
/// Typst cannot be interrupted mid-compilation, and a thread cannot be killed. A timeout or a cancellation therefore
/// <em>abandons</em> the compilation: the caller gets <see cref="TypstTimeoutException"/> or
/// <see cref="OperationCanceledException"/> at once, while the compilation runs to its end in the background, keeps its
/// concurrency slot until then, and has its result freed. This is the one guarantee the CLI engine gives and this one
/// cannot: a runaway template keeps a core busy instead of being killed.
/// </para>
/// </remarks>
internal sealed class NativeTypstCompiler(
    TypstOptions options,
    NativeTypstEngine engine,
    ILogger<NativeTypstCompiler> logger) : ITypstCompiler, IDisposable {
    private readonly SemaphoreSlim _slots = new(options.MaxConcurrency, options.MaxConcurrency);

    public async Task<TypstPdf> CompilePdfAsync(TypstCompileRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        using var activity = TypstTelemetry.Source.StartActivity("typst compile");
        activity?.SetTag(TypstTelemetry.DocumentTag, request.Source.Description);

        // Everything that can await happens before the native call: the data is serialized into memory here.
        byte[]? data = null;
        if (request.Data is not null) {
            using var buffer = new MemoryStream();
            await request.Data.WriteToAsync(buffer, cancellationToken).ConfigureAwait(false);
            data = buffer.ToArray();
        }

        var queued = Stopwatch.GetTimestamp();
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        TypstTelemetry.RecordQueue(Stopwatch.GetElapsedTime(queued));

        var started = Stopwatch.GetTimestamp();
        var abandoned = 0;
        var work = Task.Factory.StartNew(
            () => Compile(request, data), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        // The slot is held until the native call returns, even when the caller has stopped waiting: abandoned
        // compilations must still count against the limit. A result nobody will read is freed.
        _ = work.ContinueWith(task => {
            _slots.Release();
            if (Volatile.Read(ref abandoned) == 1 && task.IsCompletedSuccessfully) task.Result.Pdf.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        var timeout = request.Timeout ?? options.Timeout;
        try {
            var (pdf, warnings) = await work.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            Complete(activity, TypstOutcome.Success, started);
            var warningText = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
            if (warningText is not null) {
                logger.LogWarning("Typst compiled {Document} with warnings: {Diagnostics}", request.Source.Description, warningText);
            }

            return new TypstPdf(pdf, warningText);
        } catch (TimeoutException) {
            Abandon(work, ref abandoned);
            Complete(activity, TypstOutcome.Timeout, started);
            logger.LogError("Typst did not compile {Document} within {Timeout}; the compilation was abandoned and runs to its end",
                request.Source.Description, timeout);
            throw new TypstTimeoutException($"Typst did not compile {request.Source.Description} within {timeout}.", timeout);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            Abandon(work, ref abandoned);
            Complete(activity, TypstOutcome.Cancelled, started);
            throw;
        } catch (TypstCompilationException ex) {
            Complete(activity, TypstOutcome.CompilationError, started);
            logger.LogError("Typst rejected {Document}: {Diagnostics}", request.Source.Description, ex.Diagnostics);
            throw;
        }
    }

    /// <summary>Runs on the dedicated thread: pins the request, calls the library, unpins.</summary>
    private unsafe (Stream Pdf, IReadOnlyList<TypstDiagnostic> Warnings) Compile(TypstCompileRequest request, byte[]? data) {
        using var arena = new PinnedArena();

        var fileCount = request.Assets.Count + (data is null ? 0 : 1);
        var files = arena.Array<NativeFile>(fileCount);
        var index = 0;
        if (data is not null) {
            files[index++] = new NativeFile { Path = arena.Utf8("/data.json"), Content = arena.Pin(data) };
        }

        foreach (var asset in request.Assets) {
            files[index++] = new NativeFile { Path = arena.Utf8("/assets/" + asset.Name), Content = arena.Pin(asset.Content) };
        }

        var inputs = arena.Array<NativeInput>(request.Inputs.Count);
        index = 0;
        foreach (var (key, value) in request.Inputs) {
            inputs[index++] = new NativeInput { Key = arena.Utf8(key), Value = arena.Utf8(value) };
        }

        var standards = request.PdfStandards ?? options.PdfStandards.ToList();
        var compileOptions = new CompileOptions {
            Main = arena.Utf8(request.Source.RenderMainFile(data is not null, request.Assets)),
            Files = files,
            FilesLength = (nuint)fileCount,
            Inputs = inputs,
            InputsLength = (nuint)request.Inputs.Count,
            PdfStandards = arena.Utf8(string.Join(',', standards.Distinct().Select(standard => standard.ToCliValue()))),
            CreationTimestamp = request.CreationTimestamp?.ToUnixTimeSeconds() ?? 0,
            HasCreationTimestamp = request.CreationTimestamp is null ? (byte)0 : (byte)1,
            Tagged = options.PdfTags ? (byte)1 : (byte)0
        };

        return engine.Compile(&compileOptions, request.Source.Description);
    }

    /// <summary>
    /// Marks the compilation abandoned. Either the continuation sees the flag and frees the result, or the work has
    /// already finished and is freed here; both may happen, and disposing twice is harmless.
    /// </summary>
    private static void Abandon(Task<(Stream Pdf, IReadOnlyList<TypstDiagnostic> Warnings)> work, ref int abandoned) {
        Volatile.Write(ref abandoned, 1);
        if (work.IsCompletedSuccessfully) work.Result.Pdf.Dispose();
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
