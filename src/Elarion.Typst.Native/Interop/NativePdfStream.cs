namespace Elarion.Typst.Native.Interop;

/// <summary>
/// A read-only stream over the PDF bytes a native result owns. Nothing is copied into managed memory; disposing the
/// stream frees the native result. <see cref="UnmanagedMemoryStream"/> refuses reads after disposal, so the buffer can
/// never be read once it is freed.
/// </summary>
internal sealed unsafe class NativePdfStream : UnmanagedMemoryStream {
    private readonly ResultHandle _result;
    private bool _referenced;

    public NativePdfStream(ResultHandle result, NativeBytes pdf) {
        _result = result;
        // Hold a reference for the stream's lifetime: the handle cannot be released underneath an open stream.
        result.DangerousAddRef(ref _referenced);
        Initialize(pdf.Pointer, (long)pdf.Length, (long)pdf.Length, FileAccess.Read);
    }

    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        if (_referenced) {
            _referenced = false;
            _result.DangerousRelease();
        }

        if (disposing) _result.Dispose();
    }
}
