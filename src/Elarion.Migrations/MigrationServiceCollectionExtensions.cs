using System.Diagnostics.CodeAnalysis;
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
    /// once and calls this with the step sources and neutral options. The plan merges the embedded scripts of
    /// <see cref="MigrationOptions.AddScripts"/>, every registered <see cref="ICodeMigration"/> (see
    /// <see cref="AddCodeMigration{T}"/> and <c>[GenerateContractSetRegistration(typeof(ICodeMigration))]</c>),
    /// every <see cref="IMigrationStepSource"/> added through the options or registered as a service (EF Core
    /// migrations), into one version-ordered sequence. Applies pending migrations before the host reports ready
    /// unless <see cref="MigrationOptions.ApplyOnStartup"/> is disabled.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Configures the neutral <see cref="MigrationOptions"/>: script sources via
    /// <see cref="MigrationOptions.AddScripts"/>, further step sources via
    /// <see cref="MigrationOptions.AddStepSource"/>. May be omitted when every step comes from registered code
    /// migrations.
    /// </param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionPostgreSql(connectionString);   // provider choice for every subsystem
    /// builder.Services.AddElarionMigrations(o => o.AddScripts(typeof(Program).Assembly, "MyApp.Migrations."));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionMigrations(
        this IServiceCollection services, Action<MigrationOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MigrationOptions();
        configure?.Invoke(options);

        return services.AddElarionMigrationRunner(options, provider => {
            var logger = provider.GetService<ILogger<MigrationRunner>>();
            var codeMigrations = provider.GetServices<ICodeMigration>().ToList();
            var sources = provider.GetServices<IMigrationStepSource>().ToList();
            if (codeMigrations.Count > 0) sources.Add(new CodeMigrationStepSource(codeMigrations));

            return new MigrationRunner(
                provider.GetRequiredService<IMigrationDatabaseFactory>().Create(options, logger),
                options,
                logger,
                provider,
                sources);
        });
    }

    /// <summary>
    /// Registers <typeparamref name="T"/> as a code step of the migration plan (ADR-0081): a singleton
    /// <see cref="ICodeMigration"/>. The compile-time alternative for a whole assembly is a
    /// <c>[GenerateContractSetRegistration(typeof(ICodeMigration))]</c> method (ADR-0070). Safe to call twice.
    /// </summary>
    /// <typeparam name="T">The code migration; stateless, with a parameterless or DI-constructible constructor.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCodeMigration<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        this IServiceCollection services)
        where T : class, ICodeMigration {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICodeMigration, T>());
        return services;
    }

    /// <summary>
    /// Registers <paramref name="runnerFactory"/> as the single <see cref="IMigrationRunner"/> plus —
    /// unless <see cref="MigrationOptions.ApplyOnStartup"/> is disabled — a hosted service that applies
    /// pending migrations before the host reports ready and fails startup on error. A provider's
    /// <c>AddElarion…Migrations</c> validates its options and connection, then calls this. Fails loud on a
    /// second registration: the runner migrates exactly one database.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The migration options.</param>
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

        services.AddSingleton<IMigrationRunner>(runnerFactory);

        if (options.ApplyOnStartup)
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MigrationHostedService>());

        return services;
    }
}
