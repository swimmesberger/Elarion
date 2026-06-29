using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Elarion.Abstractions.Resilience;

namespace Elarion.Resilience;

/// <summary>
/// Registers Elarion resilience integration services.
/// </summary>
public static class ResilienceServiceCollectionExtensions {
    /// <summary>
    /// Adds the dependency-light in-memory resilience policy catalog (<see cref="IResiliencePolicyCatalog"/>).
    /// This carries no third-party runtime dependency and is what the scheduler needs to resolve policy metadata;
    /// the executable Polly-backed pipeline runner is opt-in via <c>AddMicrosoftResilienceRuntime</c> in the
    /// <c>Elarion.Resilience</c> package.
    /// </summary>
    public static IServiceCollection AddElarionResiliencePolicyCatalog(this IServiceCollection services) {
        // Note 29: TryAdd keeps host applications free to replace these services with custom implementations.
        services.TryAddSingleton<InMemoryResiliencePolicyCatalog>();
        services.TryAddSingleton<IResiliencePolicyCatalog>(sp => sp.GetRequiredService<InMemoryResiliencePolicyCatalog>());
        return services;
    }

    /// <summary>Adds framework-owned metadata for a generated resilience policy.</summary>
    public static IServiceCollection AddElarionResiliencePolicyMetadata(
        this IServiceCollection services,
        ResiliencePolicyMetadata metadata) {
        // Note 30: Metadata is separate from the executable pipeline so deferred scheduler retry can calculate future due times.
        services.AddSingleton(new ResiliencePolicyMetadataRegistration { Metadata = metadata });
        return services;
    }
}
