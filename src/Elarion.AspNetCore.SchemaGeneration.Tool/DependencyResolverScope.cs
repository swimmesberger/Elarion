using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Elarion.AspNetCore.SchemaGeneration.Tool;

internal sealed class DependencyResolverScope : IDisposable {
    private readonly AssemblyDependencyResolver _resolver;

    public DependencyResolverScope(string assemblyPath) {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
        AssemblyLoadContext.Default.Resolving += ResolveAssembly;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveUnmanagedDll;
    }

    private Assembly? ResolveAssembly(AssemblyLoadContext context, AssemblyName assemblyName) {
        var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));

        if (alreadyLoaded is not null) return alreadyLoaded;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : context.LoadFromAssemblyPath(path);
    }

    private nint ResolveUnmanagedDll(Assembly assembly, string unmanagedDllName) {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : NativeLibrary.Load(path);
    }

    public void Dispose() {
        AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveUnmanagedDll;
    }
}
