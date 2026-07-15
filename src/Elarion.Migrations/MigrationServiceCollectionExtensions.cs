using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Elarion.Migrations;

/// <summary>Shared registration for migration providers (ADR-0060).</summary>
public static class MigrationServiceCollectionExtensions {
    /// <summary>
    /// Registers <paramref name="runnerFactory"/> as the single <see cref="IMigrationRunner"/> plus —
    /// unless <see cref="MigrationOptions.ApplyOnStartup"/> is disabled — a hosted service that applies
    /// pending migrations before the host reports ready and fails startup on error. A provider's
    /// <c>AddElarion…Migrations</c> validates its options and connection, then calls this. Fails loud on a
    /// second registration: the runner migrates exactly one database.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The migration options; must carry at least one script source.</param>
    /// <param name="runnerFactory">Builds the provider's <see cref="IMigrationRunner"/> from the service provider.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionMigrationRunner(
        this IServiceCollection services,
        MigrationOptions options,
        Func<IServiceProvider, IMigrationRunner> runnerFactory) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runnerFactory);

        // Fail loud on a second registration: silently keeping the first would leave the second
        // database unmigrated. One runner per host; a second database is a second host concern.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IMigrationRunner))) {
            throw new InvalidOperationException(
                "An Elarion migration runner was already registered on this service collection; the runner migrates exactly one database.");
        }

        if (options.ScriptSources.Count == 0) {
            throw new InvalidOperationException(
                "A migration runner requires at least one script source; call options.AddScripts(assembly, resourceNamePrefix).");
        }

        services.AddSingleton<IMigrationRunner>(runnerFactory);

        if (options.ApplyOnStartup) {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MigrationHostedService>());
        }

        return services;
    }
}
