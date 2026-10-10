using System.Collections.Concurrent;
using Elarion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion;

/// <summary>Registration of the development-time error-contract check (ADR-0080).</summary>
public static class ErrorContractMonitorServiceCollectionExtensions {
    /// <summary>
    /// Registers the <see cref="IErrorContractMonitor"/> (idempotent). Whether a handler body returns a code its
    /// <see cref="ProducesErrorAttribute"/> declarations do not cover is not statically knowable, so the contract is
    /// verified at runtime — only in the Development environment, where each distinct violation is logged once as a
    /// warning. In every other environment the monitor does nothing and costs one failed-result dictionary lookup.
    /// </summary>
    public static IServiceCollection AddElarionErrorContractMonitor(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IErrorContractMonitor>(static sp =>
            sp.GetService<IHostEnvironment>()?.IsDevelopment() == true
                ? new LoggingErrorContractMonitor(sp.GetRequiredService<ILoggerFactory>().CreateLogger("Elarion.ErrorContracts"))
                : NoopErrorContractMonitor.Instance);

        return services;
    }
}

internal sealed class NoopErrorContractMonitor : IErrorContractMonitor {
    public static readonly NoopErrorContractMonitor Instance = new();

    public void Violation(string operation, AppError error, ErrorContract? declared) {
    }
}

internal sealed class LoggingErrorContractMonitor(ILogger logger) : IErrorContractMonitor {
    private readonly ConcurrentDictionary<(string Operation, string Code, ErrorKind Kind), byte> _reported = new();

    public void Violation(string operation, AppError error, ErrorContract? declared) {
        if (!_reported.TryAdd((operation, error.Code, error.Kind), 0)) return;

        if (declared is null)
            logger.LogWarning(
                "Operation {Operation} failed with error code {Code} ({Kind}) that it does not declare. " +
                "Declare it with [ProducesError] on the handler, on the decorator that returns it, or as a module or " +
                "assembly default, so the schema and generated clients describe it.",
                operation, error.Code, error.Kind);
        else
            logger.LogWarning(
                "Operation {Operation} failed with error code {Code} as {Kind} but declares it as {DeclaredKind}.",
                operation, error.Code, error.Kind, declared.Kind);
    }
}
