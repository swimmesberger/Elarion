using Elarion.Abstractions.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Elarion.Scheduling.EntityFrameworkCore;

/// <summary>Registers the EF Core/PostgreSQL cross-instance scheduler coordination.</summary>
public static class SchedulerEntityFrameworkCoreServiceCollectionExtensions {
    /// <summary>
    /// Replaces the local (single-node) occurrence coordinator with
    /// <see cref="EfCoreScheduledOccurrenceCoordinator{TDbContext}"/>, so recurring jobs on a multi-node
    /// deployment execute each occurrence on exactly one node (ADR-0025), and registers the claim-retention
    /// purge worker. Compose with <c>AddElarionScheduler</c> (either order); the context must map
    /// <see cref="SchedulerClaimEntity"/> via <c>UseElarionSchedulerClaims</c> (or
    /// <c>[GenerateElarionSchedulerClaims]</c>) in its model. Targets PostgreSQL (the window claim uses
    /// <c>pg_advisory_xact_lock</c> and <c>ON CONFLICT</c>).
    /// </summary>
    /// <typeparam name="TDbContext">The context whose model includes <see cref="SchedulerClaimEntity"/>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="SchedulerClaimsOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionSchedulerEntityFrameworkCore<TDbContext>(
        this IServiceCollection services,
        Action<SchedulerClaimsOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SchedulerClaimsOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);

        // Replace (not TryAdd) so this call is authoritative regardless of whether AddElarionScheduler
        // registered the local default first.
        services.RemoveAll<IScheduledOccurrenceCoordinator>();
        services.AddSingleton<IScheduledOccurrenceCoordinator, EfCoreScheduledOccurrenceCoordinator<TDbContext>>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, SchedulerClaimPurgeService<TDbContext>>());

        return services;
    }
}
