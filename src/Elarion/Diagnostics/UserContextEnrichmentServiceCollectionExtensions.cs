using System.Diagnostics.CodeAnalysis;
using Elarion.Abstractions.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Diagnostics;

/// <summary>
/// Host wiring for handler context enrichment
/// (<see cref="Pipeline.ObservabilityDecorator{TRequest,TResponse}"/>): configuring the built-in
/// <see cref="UserContextEnricher"/> and contributing custom <see cref="IHandlerContextEnricher"/>s.
/// </summary>
public static class UserContextEnrichmentServiceCollectionExtensions {
    /// <summary>
    /// Registers and configures the built-in <see cref="UserContextEnricher"/>. It is <b>already registered</b> when
    /// current-user support is added (<c>AddElarionClaimsCurrentUser</c>), emitting <c>user.id</c> + <c>user.roles</c>
    /// + <c>user.permissions</c> by default; call this only to change the payload
    /// (<c>o =&gt; o.IncludeEmail = true</c>), disable it (<c>o =&gt; o.Enabled = false</c>), or enable it for a host
    /// with a custom <c>ICurrentUser</c> that did not go through <c>AddElarionClaimsCurrentUser</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of the built-in enrichment payload.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddElarionUserContextEnrichment(
        this IServiceCollection services,
        Action<UserContextEnrichmentOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = new UserContextEnrichmentOptions();
        configure?.Invoke(options);
        // Last registration wins for GetService, so an explicit configure here overrides the default options a
        // current-user registration may already have registered — regardless of call order.
        services.AddSingleton(options);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IHandlerContextEnricher, UserContextEnricher>());
        return services;
    }

    /// <summary>
    /// Registers a custom <see cref="IHandlerContextEnricher"/> that contributes trace tags and log-scope items on
    /// every handler execution, alongside the built-in user-context enricher. Registered as scoped so it may inject
    /// scoped services (e.g. <see cref="Abstractions.Identity.ICurrentUser"/>); composes with other enrichers.
    /// </summary>
    /// <typeparam name="TEnricher">The enricher implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddElarionHandlerContextEnricher<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TEnricher>(
        this IServiceCollection services)
        where TEnricher : class, IHandlerContextEnricher {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IHandlerContextEnricher, TEnricher>());
        return services;
    }

    /// <summary>
    /// Registers the built-in <see cref="UserContextEnricher"/> and its default options if not already present. Called
    /// from the current-user wiring so user-context enrichment is on by default wherever <c>ICurrentUser</c> is set up.
    /// </summary>
    internal static IServiceCollection AddElarionUserContextEnricherDefault(this IServiceCollection services) {
        services.TryAddSingleton(new UserContextEnrichmentOptions());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IHandlerContextEnricher, UserContextEnricher>());
        return services;
    }
}
