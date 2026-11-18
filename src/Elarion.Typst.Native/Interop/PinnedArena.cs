using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Elarion.Typst.Native.Interop;

/// <summary>
/// Keeps everything a native call borrows alive and immovable until the call returns: managed buffers are pinned in
/// place (no copy of a multi-megabyte photo), strings are encoded once, and the arrays of structs live in native memory.
/// Disposing releases all of it, so the arena's lifetime is exactly the call's.
/// </summary>
internal sealed unsafe class PinnedArena : IDisposable {
    private readonly List<MemoryHandle> _pins = [];
    private readonly List<nint> _allocations = [];

    /// <summary>Pins <paramref name="memory"/> in place and returns a view of it.</summary>
    public NativeBytes Pin(ReadOnlyMemory<byte> memory) {
        if (memory.IsEmpty) return default;
        var handle = memory.Pin();
        _pins.Add(handle);
        return new NativeBytes((byte*)handle.Pointer, (nuint)memory.Length);
    }

    /// <summary>Encodes <paramref name="value"/> as UTF-8 and pins it.</summary>
    public NativeBytes Utf8(string? value) => string.IsNullOrEmpty(value) ? default : Pin(Encoding.UTF8.GetBytes(value));

    /// <summary>Allocates an unmanaged array of <paramref name="length"/> structs, freed with the arena.</summary>
    public T* Array<T>(int length) where T : unmanaged {
        if (length == 0) return null;
        var pointer = (T*)NativeMemory.AllocZeroed((nuint)length, (nuint)sizeof(T));
        _allocations.Add((nint)pointer);
        return pointer;
    }

    public void Dispose() {
        foreach (var pin in _pins) pin.Dispose();
        foreach (var allocation in _allocations) NativeMemory.Free((void*)allocation);
        _pins.Clear();
        _allocations.Clear();
    }
}
