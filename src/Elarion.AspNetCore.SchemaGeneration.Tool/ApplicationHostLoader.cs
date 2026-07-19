using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Hosting;

namespace Elarion.AspNetCore.SchemaGeneration.Tool;

internal sealed class ApplicationHostLoader(string assemblyPath) : IAsyncDisposable, IDisposable {
    private readonly DependencyResolverScope _dependencyResolver = new(assemblyPath);

    public async Task<IHost> LoadAsync(string[] applicationArguments) {
        using var listener = new HostingListener();

        try {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            var entryPoint = assembly.EntryPoint
                             ?? throw new InvalidOperationException(
                                 $"Assembly '{assemblyPath}' does not have an entry point.");

            await InvokeEntryPointAsync(entryPoint, applicationArguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is HostAbortedException) {
            // Expected: HostingListener throws after it captures the built host so application
            // code after builder.Build() does not run during schema generation.
        }
        catch (HostAbortedException) {
            // Expected for non-reflection paths.
        }

        return listener.GetCapturedHost();
    }

    private static async Task InvokeEntryPointAsync(MethodInfo entryPoint, string[] applicationArguments) {
        var parameters = entryPoint.GetParameters();
        object? result;
        try {
            result = parameters.Length switch {
                0 => entryPoint.Invoke(null, null),
                1 when parameters[0].ParameterType == typeof(string[]) => entryPoint.Invoke(null,
                    [applicationArguments]),
                _ => throw new InvalidOperationException(
                    $"Application entry point '{entryPoint.DeclaringType?.FullName}.{entryPoint.Name}' has an unsupported signature.")
            };
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null and not HostAbortedException) {
            // Surface the application's real startup failure instead of reflection's opaque
            // "Exception has been thrown by the target of an invocation."; HostAbortedException stays wrapped so
            // the expected-abort handling in LoadAsync keeps matching it.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable
        }

        switch (result) {
            case Task<int> intTask:
                _ = await intTask;
                break;
            case Task task:
                await task;
                break;
        }
    }

    public ValueTask DisposeAsync() {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose() {
        _dependencyResolver.Dispose();
    }
}
