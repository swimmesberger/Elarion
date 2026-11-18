using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Every type crossing the boundary is blittable, so the runtime never marshals: the source-generated stubs pass
// pointers and integers straight through, which is also what keeps them trimming- and NativeAOT-safe.
[assembly: DisableRuntimeMarshalling]

namespace Elarion.Typst.Native.Interop;

/// <summary>
/// The C ABI of <c>native/elarion-typst</c>. Mirrors <c>src/lib.rs</c>; bump <see cref="AbiVersion"/> on both sides
/// together.
/// </summary>
internal static unsafe partial class NativeMethods {
    public const string Library = "elarion_typst";

    /// <summary>The ABI this assembly was written against; a library reporting another one is refused.</summary>
    public const uint AbiVersion = 1;

    public const int StatusOk = 0;
    public const int StatusCompileError = 1;
    public const int StatusInvalidArgument = 2;
    public const int StatusPanic = 3;

    [LibraryImport(Library, EntryPoint = "elarion_typst_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint GetAbiVersion();

    [LibraryImport(Library, EntryPoint = "elarion_typst_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial NativeBytes GetTypstVersion();

    [LibraryImport(Library, EntryPoint = "elarion_typst_engine_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int CreateEngine(EngineOptions* options, nint* engine, nint* error);

    [LibraryImport(Library, EntryPoint = "elarion_typst_engine_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void DestroyEngine(nint engine);

    [LibraryImport(Library, EntryPoint = "elarion_typst_compile_pdf")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int CompilePdf(EngineHandle engine, CompileOptions* options, nint* result);

    [LibraryImport(Library, EntryPoint = "elarion_typst_result_pdf")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial NativeBytes GetResultPdf(ResultHandle result);

    [LibraryImport(Library, EntryPoint = "elarion_typst_result_diagnostics")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial NativeBytes GetResultDiagnostics(ResultHandle result);

    [LibraryImport(Library, EntryPoint = "elarion_typst_result_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void DestroyResult(nint result);
}

/// <summary>A borrowed byte view (<c>ElarionBytes</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly unsafe struct NativeBytes(byte* pointer, nuint length) {
    public readonly byte* Pointer = pointer;
    public readonly nuint Length = length;

    public ReadOnlySpan<byte> AsSpan() => new(Pointer, checked((int)Length));
}

/// <summary>A project file (<c>ElarionTypstFile</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeFile {
    public NativeBytes Path;
    public NativeBytes Content;
}

/// <summary>A <c>sys.inputs</c> entry (<c>ElarionTypstInput</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeInput {
    public NativeBytes Key;
    public NativeBytes Value;
}

/// <summary><c>ElarionTypstEngineOptions</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EngineOptions {
    public NativeBytes* FontPaths;
    public nuint FontPathsLength;
    public NativeBytes PackagePath;
    public NativeBytes PackageCachePath;
    public byte IncludeSystemFonts;
}

/// <summary><c>ElarionTypstCompileOptions</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CompileOptions {
    public NativeBytes Main;
    public NativeFile* Files;
    public nuint FilesLength;
    public NativeInput* Inputs;
    public nuint InputsLength;
    public NativeBytes PdfStandards;
    public long CreationTimestamp;
    public byte HasCreationTimestamp;
    public byte Tagged;
}

/// <summary>Owns a native engine; destroying it frees the fonts and the package cache.</summary>
internal sealed class EngineHandle() : SafeHandle(nint.Zero, ownsHandle: true) {
    public EngineHandle(nint handle) : this() => SetHandle(handle);

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle() {
        NativeMethods.DestroyEngine(handle);
        return true;
    }
}

/// <summary>Owns a native compile result: the PDF bytes and the diagnostics JSON.</summary>
internal sealed class ResultHandle() : SafeHandle(nint.Zero, ownsHandle: true) {
    public ResultHandle(nint handle) : this() => SetHandle(handle);

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle() {
        NativeMethods.DestroyResult(handle);
        return true;
    }
}
