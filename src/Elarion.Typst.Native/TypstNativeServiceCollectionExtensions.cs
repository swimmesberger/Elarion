using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Typst.Native;

/// <summary>Registers the in-process Typst engine (prototype, ADR-0094).</summary>
public static class TypstNativeServiceCollectionExtensions {
    /// <summary>
    /// Registers Typst rendering like <c>AddElarionTypst</c>, with <see cref="ITypstCompiler"/> compiled in-process by
    /// the native library instead of the CLI. Requests, templates, options and results are the same; the options'
    /// executable path is not used. The engine is created when the host starts (fonts are scanned then), and the
    /// startup check reports a missing library or an ABI mismatch.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionTypstNative(builder.Configuration.GetSection(TypstOptions.SectionName));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionTypstNative(this IServiceCollection services, Action<TypstOptions>? configure = null) {
        services.AddElarionTypst(configure);
        return UseNativeEngine(services);
    }

    /// <summary>Registers the in-process engine, binding <see cref="TypstOptions"/> from <paramref name="configuration"/>.</summary>
    public static IServiceCollection AddElarionTypstNative(
        this IServiceCollection services, IConfiguration configuration, Action<TypstOptions>? configure = null) {
        services.AddElarionTypst(configuration, configure);
        return UseNativeEngine(services);
    }

    private static IServiceCollection UseNativeEngine(IServiceCollection services) {
        // The CLI startup check looks for an executable this engine does not need.
        services.RemoveAll(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                         && descriptor.ImplementationType == typeof(TypstStartupCheckService));
        services.TryAddSingleton<NativeTypstEngine>();
        services.Replace(ServiceDescriptor.Singleton<ITypstCompiler, NativeTypstCompiler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NativeTypstStartupCheck>());
        return services;
    }

    private static void RemoveAll(this IServiceCollection services, Func<ServiceDescriptor, bool> predicate) {
        for (var i = services.Count - 1; i >= 0; i--) {
            if (predicate(services[i])) services.RemoveAt(i);
        }
    }
}

/// <summary>
/// Creates the native engine when the host starts: loading the library, checking its ABI, and scanning the fonts all
/// happen before the first document, and a failure follows <see cref="TypstOptions.StartupCheck"/>.
/// </summary>
internal sealed class NativeTypstStartupCheck(
    IServiceProvider services, TypstOptions options, ILogger<NativeTypstStartupCheck> logger) : IHostedService {
    public Task StartAsync(CancellationToken cancellationToken) {
        if (options.StartupCheck == TypstStartupCheck.None) return Task.CompletedTask;

        try {
            var engine = services.GetRequiredService<NativeTypstEngine>();
            logger.LogInformation("Typst compilations run in-process with the native engine (Typst {Version})", engine.TypstVersion);
        } catch (TypstUnavailableException ex) when (options.StartupCheck == TypstStartupCheck.Warn) {
            logger.LogWarning(ex, "The native Typst engine is unavailable; Typst compilations will fail");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
