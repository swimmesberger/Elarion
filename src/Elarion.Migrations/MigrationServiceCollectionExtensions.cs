using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Migrations;

/// <summary>Shared registration for migration providers (ADR-0060).</summary>
public static class MigrationServiceCollectionExtensions {
    /// <summary>
    /// Registers the database-neutral migration runner over the <see cref="IMigrationDatabaseFactory"/> the
    /// provider registered (for example through <c>AddElarionPostgreSql</c>): the host configures the provider
    /// once and calls this with only the script sources and neutral options. Applies pending migrations before
    /// the host reports ready unless <see cref="MigrationOptions.ApplyOnStartup"/> is disabled.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Configures the neutral <see cref="MigrationOptions"/>; must add at least one script source via
    /// <see cref="MigrationOptions.AddScripts"/>.
    /// </param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionPostgreSql(connectionString);   // provider choice for every subsystem
    /// builder.Services.AddElarionMigrations(o => o.AddScripts(typeof(Program).Assembly, "MyApp.Migrations."));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionMigrations(
        this IServiceCollection services, Action<MigrationOptions> configure) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new MigrationOptions();
        configure(options);

        return services.AddElarionMigrationRunner(options, provider => new MigrationRunner(
            provider.GetRequiredService<IMigrationDatabaseFactory>().Create(
                options, provider.GetService<ILogger<MigrationRunner>>()),
            options,
            provider.GetService<ILogger<MigrationRunner>>()));
    }

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
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IMigrationRunner)))
            throw new InvalidOperationException(
                "An Elarion migration runner was already registered on this service collection; the runner migrates exactly one database.");

        if (options.ScriptSources.Count == 0)
            throw new InvalidOperationException(
                "A migration runner requires at least one script source; call options.AddScripts(assembly, resourceNamePrefix).");

        services.AddSingleton<IMigrationRunner>(runnerFactory);

        if (options.ApplyOnStartup)
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MigrationHostedService>());

        return services;
    }
}
