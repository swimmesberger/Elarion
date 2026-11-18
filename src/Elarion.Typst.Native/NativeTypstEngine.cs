using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elarion.Typst.Native.Interop;

namespace Elarion.Typst.Native;

/// <summary>
/// The process-wide native engine: the loaded library, the fonts (scanned once), and the package file cache. Created
/// at startup by <c>AddElarionTypstNative()</c>, so a missing library or an ABI mismatch surfaces before the first
/// document.
/// </summary>
internal sealed unsafe class NativeTypstEngine : IDisposable {
    private readonly EngineHandle _engine;

    public NativeTypstEngine(TypstOptions options, TypstToolchain toolchain) {
        uint abi;
        try {
            abi = NativeMethods.GetAbiVersion();
        } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) {
            throw new TypstUnavailableException(
                $"The native Typst library '{NativeMethods.Library}' could not be loaded for this platform.", ex);
        }

        if (abi != NativeMethods.AbiVersion)
            throw new TypstUnavailableException(
                $"The native Typst library implements ABI {abi}, but Elarion.Typst.Native expects {NativeMethods.AbiVersion}.");

        TypstVersion = Encoding.UTF8.GetString(NativeMethods.GetTypstVersion().AsSpan());

        using var arena = new PinnedArena();
        var fontPaths = arena.Array<NativeBytes>(toolchain.FontPaths.Count);
        for (var i = 0; i < toolchain.FontPaths.Count; i++) fontPaths[i] = arena.Utf8(toolchain.FontPaths[i]);
        var engineOptions = new EngineOptions {
            FontPaths = fontPaths,
            FontPathsLength = (nuint)toolchain.FontPaths.Count,
            PackagePath = arena.Utf8(toolchain.PackagePath),
            PackageCachePath = arena.Utf8(toolchain.PackageCachePath),
            IncludeSystemFonts = options.IgnoreSystemFonts ? (byte)0 : (byte)1
        };

        nint engine = 0, error = 0;
        var status = NativeMethods.CreateEngine(&engineOptions, &engine, &error);
        if (status != NativeMethods.StatusOk) {
            using var failure = new ResultHandle(error);
            throw new TypstUnavailableException(
                "The native Typst engine could not be created: " + string.Join("; ", ReadDiagnostics(failure)));
        }

        _engine = new EngineHandle(engine);
    }

    /// <summary>The Typst version the native library embeds.</summary>
    public string TypstVersion { get; }

    /// <summary>
    /// Compiles synchronously on the calling thread and returns the PDF as a stream over native memory. The caller
    /// keeps every buffer <paramref name="options"/> points to pinned until this returns.
    /// </summary>
    public (Stream Pdf, IReadOnlyList<TypstDiagnostic> Warnings) Compile(CompileOptions* options, string description) {
        nint result = 0;
        var status = NativeMethods.CompilePdf(_engine, options, &result);
        var handle = new ResultHandle(result);
        try {
            var diagnostics = ReadDiagnostics(handle);
            if (status != NativeMethods.StatusOk) {
                var reason = status switch {
                    NativeMethods.StatusPanic => "Typst failed internally on",
                    NativeMethods.StatusInvalidArgument => "Typst could not accept",
                    _ => "Typst rejected"
                };
                throw new TypstCompilationException($"{reason} {description}.", diagnostics);
            }

            var pdf = new NativePdfStream(handle, NativeMethods.GetResultPdf(handle));
            return (pdf, diagnostics);
        } catch {
            handle.Dispose();
            throw;
        }
    }

    private static TypstDiagnostic[] ReadDiagnostics(ResultHandle result) {
        if (result.IsInvalid) return [];
        var json = NativeMethods.GetResultDiagnostics(result).AsSpan();
        return json.IsEmpty ? [] : JsonSerializer.Deserialize(json, NativeJsonContext.Default.TypstDiagnosticArray) ?? [];
    }

    public void Dispose() => _engine.Dispose();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TypstDiagnostic[]))]
internal sealed partial class NativeJsonContext : JsonSerializerContext;
