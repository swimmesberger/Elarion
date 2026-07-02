using Elarion.Abstractions.Serialization;
using Elarion.Abstractions.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Validation;

namespace Elarion.Validation;

/// <summary>
/// Service registration helpers for declarative request validation (ADR-0027).
/// </summary>
public static class ElarionValidationServiceCollectionExtensions {
    /// <summary>
    /// Adds the default <c>Microsoft.Extensions.Validation</c>-backed <see cref="IRequestValidator"/>
    /// implementation. Idempotent: calling it again neither duplicates the base validation services nor
    /// replaces an already-registered <see cref="IRequestValidator"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional <see cref="ValidationOptions"/> configuration (e.g. <c>MaxDepth</c>).</param>
    public static IServiceCollection AddElarionValidation(this IServiceCollection services, Action<ValidationOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        // Guarded so repeated registration does not re-run Microsoft's AddValidation (which appends its
        // runtime parameter resolver on every call); a repeated call still applies the extra configure.
        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(ElarionValidationMarker))) {
            services.AddSingleton<ElarionValidationMarker>();
            services.AddValidation(configure);
        } else if (configure is not null) {
            services.Configure(configure);
        }

        services.AddElarionJson();
        services.TryAddScoped<IRequestValidator, MicrosoftRequestValidator>();

        return services;
    }

    /// <summary>
    /// Registers an <see cref="IValidatableInfoResolver"/> contributing validation metadata for a set of
    /// request types, inserted ahead of previously registered resolvers (resolution is first-match-wins).
    /// This is what generated module code calls to plug in its source-generated resolver.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="resolver">The resolver to register.</param>
    public static IServiceCollection AddElarionValidationResolver(this IServiceCollection services, IValidatableInfoResolver resolver) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resolver);

        services.Configure<ValidationOptions>(options => options.Resolvers.Insert(0, resolver));

        return services;
    }

    /// <summary>Marker guarding <see cref="AddElarionValidation"/> against duplicate base registration.</summary>
    private sealed class ElarionValidationMarker;
}
