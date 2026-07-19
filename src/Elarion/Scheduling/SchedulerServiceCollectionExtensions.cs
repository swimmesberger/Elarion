using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Elarion.Abstractions.Scheduling;
using Elarion.Resilience;
using Elarion.Substitution;

namespace Elarion.Scheduling;

/// <summary>
/// Registers the in-memory scheduler runtime.
/// </summary>
public static class SchedulerServiceCollectionExtensions {
    /// <summary>
    /// Adds the scheduler hosted service and runtime scheduling API using explicit options.
    /// </summary>
    /// <remarks>
    /// Registers <see cref="IJobScheduler"/>, <see cref="IJobSchedulerInspector"/>,
    /// <see cref="TimeProvider"/> when absent, and the hosted scheduler loop. Generated
    /// scheduled job descriptor registration must still be called separately.
    /// </remarks>
    public static IServiceCollection AddElarionScheduler(
        this IServiceCollection services,
        SchedulerOptions? options = null) {
        options ??= new SchedulerOptions();
        // The scheduler needs the (dependency-light) policy catalog to resolve job retry policies. The Polly-backed
        // pipeline runner that actually executes deferred retries is opt-in via the Elarion.Resilience package's
        // AddElarionResilience() — kept out of core so scheduling does not force Microsoft.Extensions.Resilience.
        services.AddElarionResiliencePolicyCatalog();
        // The scheduler resolves ${...} schedule variables through IVariableSource; default to the
        // config-backed source (observable, so reloads drive live reschedule). Register a different
        // IVariableSource first to override.
        services.AddElarionVariableSubstitution();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        // Single-node default: every occurrence is claimed locally. The EF Core/PostgreSQL coordinator
        // replaces this so a multi-node deployment executes each recurring occurrence on exactly one node.
        services.TryAddSingleton<IScheduledOccurrenceCoordinator, LocalScheduledOccurrenceCoordinator>();

        services.TryAddSingleton<InMemoryScheduler>();
        services.TryAddSingleton<IJobScheduler>(sp => sp.GetRequiredService<InMemoryScheduler>());
        services.TryAddSingleton<IJobSchedulerInspector>(sp => sp.GetRequiredService<InMemoryScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<InMemoryScheduler>());
        return services;
    }

    /// <summary>
    /// Adds the scheduler hosted service using values from the <c>Scheduler</c> configuration section.
    /// </summary>
    /// <remarks>
    /// Reads <c>Scheduler:Enabled</c>, <c>Scheduler:MaxConcurrentExecutions</c>,
    /// <c>Scheduler:MaxRetainedCompletedJobs</c>, and
    /// <c>Scheduler:MaxMisfireCatchUpRuns</c>. Invalid boolean or integer values throw during
    /// service registration.
    /// </remarks>
    public static IServiceCollection AddElarionScheduler(
        this IServiceCollection services,
        IConfiguration configuration) {
        var options = new SchedulerOptions {
            Enabled = ReadBool(configuration, "Scheduler:Enabled", true),
            MaxConcurrentExecutions = Math.Max(1, ReadInt(
                configuration,
                "Scheduler:MaxConcurrentExecutions",
                Math.Max(1, Environment.ProcessorCount))),
            MaxRetainedCompletedJobs = Math.Max(0, ReadInt(
                configuration,
                "Scheduler:MaxRetainedCompletedJobs",
                1024)),
            MaxMisfireCatchUpRuns = Math.Max(0, ReadInt(
                configuration,
                "Scheduler:MaxMisfireCatchUpRuns",
                32))
        };

        return services.AddElarionScheduler(options);
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool defaultValue) {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;

        if (bool.TryParse(value, out var parsed)) return parsed;

        throw new InvalidOperationException($"Configuration value '{key}' must be a boolean.");
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue) {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;

        if (int.TryParse(value, out var parsed)) return parsed;

        throw new InvalidOperationException($"Configuration value '{key}' must be an integer.");
    }
}
