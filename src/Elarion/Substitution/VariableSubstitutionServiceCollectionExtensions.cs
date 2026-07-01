using Elarion.Abstractions.Substitution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Substitution;

/// <summary>Registers the reusable variable-substitution seam.</summary>
public static class VariableSubstitutionServiceCollectionExtensions {
    /// <summary>
    /// Registers a default <see cref="IVariableSource"/> backed by <c>IConfiguration</c>, so any subsystem can
    /// inject <see cref="IVariableSource"/> and resolve <c>${key:-default}</c> placeholders with
    /// <see cref="VariableSubstitution"/>. Pointed at configuration, it transparently sees settings,
    /// environment variables, and appsettings; register a different <see cref="IVariableSource"/> first to
    /// override the source.
    /// </summary>
    public static IServiceCollection AddElarionVariableSubstitution(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IVariableSource, ConfigurationVariableSource>();
        return services;
    }
}
