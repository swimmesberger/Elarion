using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.WebDav.EntityFrameworkCore;

/// <summary>Registers the EF-backed published-collection store (ADR-0111).</summary>
public static class WebDavEntityFrameworkCoreServiceCollectionExtensions {
    /// <summary>
    /// Registers the WebDAV core (<see cref="WebDavServiceCollectionExtensions.AddElarionWebDav"/>) with the durable
    /// <see cref="IDavCollectionStore"/> over <typeparamref name="TDbContext"/> (scoped, so it joins the context's
    /// transaction), replacing any other store. The context must map the store: annotate it with
    /// <c>[GenerateElarionDavStore]</c> or call <c>modelBuilder.UseElarionDavStore()</c>, and create the tables with a
    /// migration. PostgreSQL only.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section holding the <see cref="WebDavOptions"/> keys, or <see langword="null"/>.</param>
    /// <param name="configure">Adjusts the options after binding.</param>
    public static IServiceCollection AddElarionWebDavEntityFrameworkCore<TDbContext>(this IServiceCollection services,
        IConfiguration? configuration = null, Action<WebDavOptions>? configure = null) where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);
        services.AddElarionWebDav(configuration, configure);
        services.RemoveAll<IDavCollectionStore>();
        services.AddScoped<IDavCollectionStore, EfCoreDavCollectionStore<TDbContext>>();
        return services;
    }
}
