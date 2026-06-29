using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Elarion.Abstractions.Resilience;

namespace Elarion.Resilience;

/// <summary>
/// Registers the Microsoft/Polly-backed resilience runtime. This is the heavy half of Elarion resilience
/// (it pulls <c>Microsoft.Extensions.Resilience</c>); the policy catalog and metadata registration are
/// dependency-light and live in Elarion core, so referencing this package is what opts an application into the
/// executable pipeline runner.
/// </summary>
public static class MicrosoftResilienceServiceCollectionExtensions {
    /// <summary>Adds the default Microsoft/Polly-backed resilience runtime (policy catalog + pipeline runner).</summary>
    public static IServiceCollection AddMicrosoftResilienceRuntime(this IServiceCollection services) {
        // The lightweight policy catalog lives in core; ensure it is present, then add the Polly-backed runner.
        services.AddElarionResiliencePolicyCatalog();
        services.TryAddSingleton<IResiliencePipelineRunner, MicrosoftResiliencePipelineRunner>();

        return services;
    }
}
