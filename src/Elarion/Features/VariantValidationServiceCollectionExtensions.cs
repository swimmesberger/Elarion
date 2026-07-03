using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Features;

/// <summary>Registration for the variant registry validator.</summary>
public static class VariantValidationServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="VariantConfigurationValidator"/>: startup + configuration-reload validation of the
    /// seeded variant registry (configured values must be offered; platform variant contracts must be wired).
    /// Seed the catalog first — <c>services.AddElarionVariantCatalog(ElarionVariants.All)</c> — and pass
    /// <see cref="VariantValidationOptions.Strict"/> to fail startup on findings instead of warning.
    /// </summary>
    public static IServiceCollection AddElarionVariantValidation(
        this IServiceCollection services,
        Action<VariantValidationOptions>? configure = null) {
        var options = new VariantValidationOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.AddHostedService<VariantConfigurationValidator>();

        return services;
    }
}
