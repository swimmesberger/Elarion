using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Abstractions.Serialization;

/// <summary>
/// Registration for Elarion's canonical JSON serialization: the single <see cref="IElarionJsonSerialization"/>
/// accessor that every subsystem reads, and the host/layer hook for contributing to it.
/// </summary>
public static class ElarionJsonServiceCollectionExtensions {
    /// <summary>
    /// Registers the <see cref="IElarionJsonSerialization"/> accessor (idempotent). Called by every subsystem
    /// <c>Add…</c> that needs to (de)serialize, so a subsystem never assumes a bare <see cref="System.Text.Json.JsonSerializerOptions"/>
    /// is in DI. Deliberately registers <em>no</em> bare <c>JsonSerializerOptions</c> — consumers depend on the
    /// accessor instead, so Elarion never collides with a host's own <c>JsonSerializerOptions</c> registration.
    /// </summary>
    public static IServiceCollection AddElarionJson(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        // Factory registration: constructs the internal accessor directly, so the container never needs to reflect
        // over an internal type's constructor.
        services.TryAddSingleton<IElarionJsonSerialization>(sp =>
            new ElarionJsonSerialization(sp.GetServices<ElarionJsonConfigurator>()));

        return services;
    }

    /// <summary>
    /// Contributes to the canonical <see cref="ElarionJsonOptions"/> (and ensures the accessor is registered).
    /// Contributions accumulate in registration order and are applied when the options are first materialized, so
    /// several layers — transports, the module bootstrapper, the host — each add their resolvers and knobs.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.ConfigureElarionJson(o => {
    ///     o.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    ///     o.TypeInfoResolvers.Add(MyContext.Default);
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection ConfigureElarionJson(
        this IServiceCollection services,
        Action<ElarionJsonOptions> configure) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddElarionJson();
        services.AddSingleton(new ElarionJsonConfigurator(configure));

        return services;
    }
}
