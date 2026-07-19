using Elarion.Settings.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Elarion.Settings.PostgreSql;

/// <summary>Registers the PostgreSQL <c>LISTEN/NOTIFY</c> change source for Elarion settings.</summary>
public static class SettingsPostgreSqlServiceCollectionExtensions {
    /// <summary>
    /// Replaces the in-process settings change source with the cross-instance PostgreSQL
    /// <c>LISTEN/NOTIFY</c> source, so a settings write on one node fires <c>IChangeToken</c> watchers — and
    /// everything built on them: <c>ISettingsManager.Watch</c>, the settings <c>IConfiguration</c> provider,
    /// and the scheduler's <c>${...}</c> live rescheduling — on every node. Also replaces the EF Core store's
    /// change notifier with one that publishes on the store's own connection, so a write inside a transaction
    /// is announced only on commit (PostgreSQL <c>NOTIFY</c> is transactional).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">
    /// The connection string of the PostgreSQL database the settings live in; the source opens its own
    /// dedicated listen connection there.
    /// </param>
    /// <param name="configure">Optional configuration of <see cref="PostgreSqlSettingsChangeOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// Compose with <c>AddElarionSettingsEntityFrameworkCore&lt;TDbContext&gt;</c> (either order); the context
    /// must target the same database as <paramref name="connectionString"/>. The multi-instance startup warning
    /// the EF store otherwise logs does not fire once this source is registered.
    /// </remarks>
    public static IServiceCollection AddElarionPostgreSqlSettingsChanges(
        this IServiceCollection services,
        string connectionString,
        Action<PostgreSqlSettingsChangeOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return AddCore(
            services,
            configure,
            provider => new PostgreSqlSettingsChangeSource(
                NpgsqlDataSource.Create(connectionString),
                true,
                provider.GetRequiredService<PostgreSqlSettingsChangeOptions>(),
                provider.GetRequiredService<ILogger<PostgreSqlSettingsChangeSource>>()));
    }

    /// <summary>
    /// The <see cref="NpgsqlDataSource"/> overload of
    /// <see cref="AddElarionPostgreSqlSettingsChanges(IServiceCollection, string, Action{PostgreSqlSettingsChangeOptions}?)"/>
    /// for hosts that already manage a data source; the source borrows it and does not dispose it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dataSource">The data source of the PostgreSQL database the settings live in.</param>
    /// <param name="configure">Optional configuration of <see cref="PostgreSqlSettingsChangeOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionPostgreSqlSettingsChanges(
        this IServiceCollection services,
        NpgsqlDataSource dataSource,
        Action<PostgreSqlSettingsChangeOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        return AddCore(
            services,
            configure,
            provider => new PostgreSqlSettingsChangeSource(
                dataSource,
                false,
                provider.GetRequiredService<PostgreSqlSettingsChangeOptions>(),
                provider.GetRequiredService<ILogger<PostgreSqlSettingsChangeSource>>()));
    }

    private static IServiceCollection AddCore(
        IServiceCollection services,
        Action<PostgreSqlSettingsChangeOptions>? configure,
        Func<IServiceProvider, PostgreSqlSettingsChangeSource> sourceFactory) {
        var options = new PostgreSqlSettingsChangeOptions();
        configure?.Invoke(options);

        services.AddElarionSettings();
        services.TryAddSingleton(options);
        services.TryAddSingleton(sourceFactory);

        // Replace (not TryAdd) the watch/publish seams so this call is authoritative regardless of whether
        // AddElarionSettings/AddElarionSettingsEntityFrameworkCore ran first.
        services.RemoveAll<ISettingsChangeSource>();
        services.RemoveAll<ISettingsChangePublisher>();
        services.AddSingleton<ISettingsChangeSource>(provider =>
            provider.GetRequiredService<PostgreSqlSettingsChangeSource>());
        services.AddSingleton<ISettingsChangePublisher>(provider =>
            provider.GetRequiredService<PostgreSqlSettingsChangeSource>());

        // The EF store notifies on its own connection so transactional writes are commit-gated by NOTIFY.
        services.RemoveAll<IEfCoreSettingsChangeNotifier>();
        services.AddSingleton<IEfCoreSettingsChangeNotifier, PostgreSqlTransactionalSettingsChangeNotifier>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, PostgreSqlSettingsChangeListener>());

        return services;
    }
}
