using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.AppPasswords.EntityFrameworkCore;

/// <summary>Registers the EF-backed app-password store (ADR-0111).</summary>
public static class AppPasswordEntityFrameworkCoreServiceCollectionExtensions {
    /// <summary>
    /// Registers app passwords (<see cref="AppPasswordServiceCollectionExtensions.AddElarionAppPasswords"/>) with the
    /// durable <see cref="IAppPasswordStore"/> over <typeparamref name="TDbContext"/>, replacing any other store. The
    /// context must map the app-password table: annotate it with <c>[GenerateElarionAppPasswords]</c> or call
    /// <c>modelBuilder.UseElarionAppPasswords()</c>, and create the table with a migration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section holding the <see cref="AppPasswordOptions"/> keys, or <see langword="null"/>.</param>
    /// <param name="configure">Adjusts the options after binding.</param>
    public static IServiceCollection AddElarionAppPasswordsEntityFrameworkCore<TDbContext>(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<AppPasswordOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);
        services.AddElarionAppPasswords(configuration, configure);
        services.RemoveAll<IAppPasswordStore>();
        services.AddSingleton<IAppPasswordStore, EfCoreAppPasswordStore<TDbContext>>();
        return services;
    }
}
