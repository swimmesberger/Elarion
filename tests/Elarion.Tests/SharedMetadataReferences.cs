using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Elarion.Tests;

/// <summary>
/// One <see cref="PortableExecutableReference"/> per assembly path, shared by every Roslyn compilation the tests
/// build. Roslyn reuses metadata only between compilations that hold the same reference instance; creating a
/// fresh reference for each of the several hundred trusted-platform assemblies on every compilation made the
/// generator and analyzer tests hold tens of gigabytes when run in parallel.
/// </summary>
internal static class SharedMetadataReferences {
    private static readonly ConcurrentDictionary<string, PortableExecutableReference> Cache = new(StringComparer.Ordinal);

    /// <summary>The shared reference for the assembly at <paramref name="path"/>.</summary>
    public static PortableExecutableReference FromFile(string path) =>
        Cache.GetOrAdd(path, static p => MetadataReference.CreateFromFile(p));
}
