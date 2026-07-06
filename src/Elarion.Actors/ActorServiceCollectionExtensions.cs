using Elarion.Actors.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Actors;

/// <summary>
/// Registers the in-memory actor runtime. Generated per-module <c>Add{Module}Actors</c> extensions
/// call these; hosts only call them directly for hand-rolled registrations.
/// </summary>
public static class ActorServiceCollectionExtensions {
    /// <summary>
    /// Adds the actor system (<see cref="IActorSystem"/>), the shutdown-drain hosted service, and a
    /// default <see cref="TimeProvider"/> when absent. Idempotent.
    /// </summary>
    public static IServiceCollection AddElarionActorSystem(this IServiceCollection services) {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(static serviceProvider => new ActorSystem(
            serviceProvider.GetServices<ActorRegistration>(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));
        services.TryAddSingleton<IActorSystem>(static serviceProvider =>
            serviceProvider.GetRequiredService<ActorSystem>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ActorSystemLifecycleHost>());
        return services;
    }

    /// <summary>Adds one actor registration (normally called by generated code).</summary>
    public static IServiceCollection AddElarionActor(this IServiceCollection services, ActorRegistration registration) {
        services.AddSingleton(registration);
        return services;
    }
}
