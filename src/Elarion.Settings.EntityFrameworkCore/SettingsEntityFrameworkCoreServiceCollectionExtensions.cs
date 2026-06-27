using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Settings.EntityFrameworkCore;

/// <summary>Registers the EF Core database backend for Elarion settings.</summary>
public static class SettingsEntityFrameworkCoreServiceCollectionExtensions {
    /// <summary>
    /// Registers the settings foundation (change source, manager, time provider) and uses
    /// <see cref="EfCoreSettingsStore{TDbContext}"/> as the <see cref="ISettingsStore"/>, replacing the
    /// in-process default so this call is authoritative regardless of order. The context must map
    /// <see cref="Setting"/> via <c>UseElarionSettings</c> in its <c>OnModelCreating</c>.
    /// </summary>
    /// <typeparam name="TDbContext">The context whose model includes <see cref="Setting"/>.</typeparam>
    public static IServiceCollection AddElarionSettingsEntityFrameworkCore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElarionSettings();
        services.RemoveAll<ISettingsStore>();
        services.AddScoped<ISettingsStore, EfCoreSettingsStore<TDbContext>>();

        return services;
    }
}
