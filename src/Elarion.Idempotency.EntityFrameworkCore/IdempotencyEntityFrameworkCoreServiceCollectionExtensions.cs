using Elarion.Abstractions.Idempotency;
using Elarion.EntityFrameworkCore.UnitOfWork;
using Elarion.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Idempotency.EntityFrameworkCore;

/// <summary>
/// Wires the durable EF Core idempotency store over <typeparamref name="TDbContext"/>: it replaces the in-memory
/// defaults with the transactional store and unit of work, and runs the retention purge worker. The host maps the
/// table in <c>OnModelCreating</c> (via <c>[GenerateElarionIdempotencyKeys]</c> or
/// <c>ApplyElarionIdempotencyKeys</c>) and owns the migration.
/// </summary>
public static class IdempotencyEntityFrameworkCoreServiceCollectionExtensions {
    /// <summary>Registers the EF Core idempotency store, unit of work, and purge worker.</summary>
    public static IServiceCollection AddElarionIdempotencyEntityFrameworkCore<TDbContext>(
        this IServiceCollection services,
        Action<IdempotencyPurgeOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        // Core defaults (scoped key accessor, dispatch-scope initializer, TimeProvider); then swap the store and
        // unit of work for the transactional EF implementations.
        services.AddElarionIdempotency();

        var options = new IdempotencyPurgeOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.RemoveAll<IIdempotencyStore>();
        services.AddScoped<IIdempotencyStore, EfCoreIdempotencyStore<TDbContext>>();

        services.AddElarionUnitOfWork<TDbContext>();
        services.AddHostedService<IdempotencyKeyPurgeService>();

        return services;
    }
}
